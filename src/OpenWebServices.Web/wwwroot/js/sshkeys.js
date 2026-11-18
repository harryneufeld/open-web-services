// Generates an Ed25519 SSH key pair in the browser. The private key is offered as a file download and never leaves
// the browser; only the public key line is returned to ows.
//
// WebCrypto Ed25519 is used when the browser offers it. It only exists in secure contexts (HTTPS or localhost) and
// ows often runs on plain HTTP in a home network, so a small Ed25519 public-key derivation (RFC 8032) is the
// fallback there. The randomness always comes from crypto.getRandomValues, which works in every context.
(() => {
    // ---------- byte helpers ----------
    const enc = new TextEncoder();
    const u32 = (n) => new Uint8Array([(n >>> 24) & 255, (n >>> 16) & 255, (n >>> 8) & 255, n & 255]);
    const concat = (...parts) => {
        const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
        let offset = 0;
        for (const p of parts) { out.set(p, offset); offset += p.length; }
        return out;
    };
    const sshString = (bytes) => concat(u32(bytes.length), typeof bytes === "string" ? enc.encode(bytes) : bytes);
    const str = (s) => sshString(enc.encode(s));
    const base64 = (bytes) => {
        let binary = "";
        for (const b of bytes) binary += String.fromCharCode(b);
        return btoa(binary);
    };

    // ---------- SHA-512 (fallback only) ----------
    const MASK64 = (1n << 64n) - 1n;
    const firstPrimes = (count) => {
        const primes = [];
        for (let n = 2; primes.length < count; n++)
            if (primes.every((p) => n % p !== 0)) primes.push(n);
        return primes;
    };
    const iroot = (value, k) => { // floor(value^(1/k)) by Newton's method
        const kb = BigInt(k);
        let x = 1n << BigInt(Math.ceil(value.toString(2).length / k) + 1);
        for (;;) {
            const y = ((kb - 1n) * x + value / x ** (kb - 1n)) / kb;
            if (y >= x) return x;
            x = y;
        }
    };
    // The constants are the fractional parts of square and cube roots of the first primes (FIPS 180-4),
    // computed instead of typed in.
    const primes = firstPrimes(80);
    const K = primes.map((p) => iroot(BigInt(p) << 192n, 3) & MASK64);
    const H0 = primes.slice(0, 8).map((p) => iroot(BigInt(p) << 128n, 2) & MASK64);
    const rotr = (x, n) => ((x >> n) | (x << (64n - n))) & MASK64;

    function sha512(message) {
        const bitLength = BigInt(message.length) * 8n;
        const padded = new Uint8Array(Math.ceil((message.length + 17) / 128) * 128);
        padded.set(message);
        padded[message.length] = 0x80;
        for (let i = 0; i < 16; i++)
            padded[padded.length - 1 - i] = Number((bitLength >> BigInt(8 * i)) & 255n);

        const h = [...H0];
        const w = new Array(80);
        for (let block = 0; block < padded.length; block += 128) {
            for (let t = 0; t < 16; t++) {
                let v = 0n;
                for (let j = 0; j < 8; j++) v = (v << 8n) | BigInt(padded[block + t * 8 + j]);
                w[t] = v;
            }
            for (let t = 16; t < 80; t++) {
                const s0 = rotr(w[t - 15], 1n) ^ rotr(w[t - 15], 8n) ^ (w[t - 15] >> 7n);
                const s1 = rotr(w[t - 2], 19n) ^ rotr(w[t - 2], 61n) ^ (w[t - 2] >> 6n);
                w[t] = (w[t - 16] + s0 + w[t - 7] + s1) & MASK64;
            }
            let [a, b, c, d, e, f, g, hh] = h;
            for (let t = 0; t < 80; t++) {
                const t1 = (hh + (rotr(e, 14n) ^ rotr(e, 18n) ^ rotr(e, 41n)) + ((e & f) ^ (~e & MASK64 & g)) + K[t] + w[t]) & MASK64;
                const t2 = ((rotr(a, 28n) ^ rotr(a, 34n) ^ rotr(a, 39n)) + ((a & b) ^ (a & c) ^ (b & c))) & MASK64;
                [hh, g, f, e, d, c, b, a] = [g, f, e, (d + t1) & MASK64, c, b, a, (t1 + t2) & MASK64];
            }
            [a, b, c, d, e, f, g, hh].forEach((v, i) => { h[i] = (h[i] + v) & MASK64; });
        }
        const out = new Uint8Array(64);
        h.forEach((v, i) => { for (let j = 0; j < 8; j++) out[i * 8 + j] = Number((v >> BigInt(56 - 8 * j)) & 255n); });
        return out;
    }

    // ---------- Ed25519 public key from seed (fallback only) ----------
    const P = (1n << 255n) - 19n;
    const mod = (x) => ((x % P) + P) % P;
    const pow = (base, exp) => {
        let result = 1n;
        base = mod(base);
        while (exp > 0n) {
            if (exp & 1n) result = (result * base) % P;
            base = (base * base) % P;
            exp >>= 1n;
        }
        return result;
    };
    const inv = (x) => pow(x, P - 2n);
    const D = mod(-121665n * inv(121666n));
    const BX = 15112221349535400772501151409588531511454012693041857206046113283949847762202n;
    const BY = 46316835694926478169428394003475163141307993866256225615783033603165251855960n;

    // Extended twisted Edwards coordinates (X, Y, Z, T), unified addition (a = -1).
    const add = ([x1, y1, z1, t1], [x2, y2, z2, t2]) => {
        const a = mod((y1 - x1) * (y2 - x2));
        const b = mod((y1 + x1) * (y2 + x2));
        const c = mod(2n * D * t1 * t2);
        const d = mod(2n * z1 * z2);
        const e = b - a, f = d - c, g = d + c, h = b + a;
        return [mod(e * f), mod(g * h), mod(f * g), mod(e * h)];
    };

    function ed25519PublicKey(seed) {
        const hash = sha512(seed);
        hash[0] &= 248;
        hash[31] &= 127;
        hash[31] |= 64;
        let scalar = 0n;
        for (let i = 31; i >= 0; i--) scalar = (scalar << 8n) | BigInt(hash[i]);

        let result = [0n, 1n, 1n, 0n];
        let addend = [BX, BY, 1n, mod(BX * BY)];
        while (scalar > 0n) {
            if (scalar & 1n) result = add(result, addend);
            addend = add(addend, addend);
            scalar >>= 1n;
        }
        const zInv = inv(result[2]);
        const x = mod(result[0] * zInv);
        let y = mod(result[1] * zInv);
        const out = new Uint8Array(32);
        for (let i = 0; i < 32; i++) { out[i] = Number(y & 255n); y >>= 8n; }
        if (x & 1n) out[31] |= 0x80;
        return out;
    }

    // ---------- key pair ----------
    async function webCryptoPair() {
        if (!window.isSecureContext || !crypto.subtle) return null;
        try {
            const pair = await crypto.subtle.generateKey({ name: "Ed25519" }, true, ["sign", "verify"]);
            const pkcs8 = new Uint8Array(await crypto.subtle.exportKey("pkcs8", pair.privateKey));
            const publicKey = new Uint8Array(await crypto.subtle.exportKey("raw", pair.publicKey));
            return { seed: pkcs8.slice(pkcs8.length - 32), publicKey };
        } catch {
            return null; // Ed25519 not supported by this browser
        }
    }

    function fallbackPair() {
        const seed = crypto.getRandomValues(new Uint8Array(32));
        return { seed, publicKey: ed25519PublicKey(seed) };
    }

    // OpenSSH's own private key format ("openssh-key-v1"), unencrypted: ssh-keygen -p adds a passphrase.
    function openSshPrivateKey(seed, publicKey, comment) {
        const publicBlob = concat(str("ssh-ed25519"), sshString(publicKey));
        const check = crypto.getRandomValues(new Uint8Array(4));
        let section = concat(check, check, str("ssh-ed25519"), sshString(publicKey),
            sshString(concat(seed, publicKey)), str(comment));
        const padding = [];
        for (let i = 1; (section.length + padding.length) % 8 !== 0; i++) padding.push(i);
        section = concat(section, new Uint8Array(padding));

        const body = concat(enc.encode("openssh-key-v1\0"), str("none"), str("none"), str(""), u32(1),
            sshString(publicBlob), sshString(section));
        const lines = base64(body).match(/.{1,70}/g).join("\n");
        return `-----BEGIN OPENSSH PRIVATE KEY-----\n${lines}\n-----END OPENSSH PRIVATE KEY-----\n`;
    }

    function download(fileName, text) {
        const url = URL.createObjectURL(new Blob([text], { type: "application/octet-stream" }));
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName;
        link.rel = "noopener";
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
    }

    /** Generates a key pair, downloads the private key and returns the public key line. */
    window.owsGenerateSshKey = async (comment, fileName) => {
        const pair = (await webCryptoPair()) ?? fallbackPair();
        const publicLine = `ssh-ed25519 ${base64(concat(str("ssh-ed25519"), sshString(pair.publicKey)))} ${comment}`;
        download(fileName, openSshPrivateKey(pair.seed, pair.publicKey, comment));
        pair.seed.fill(0);
        return publicLine;
    };

    // For tests: the fallback derivation, so it can be checked against RFC 8032 vectors.
    window.owsEd25519PublicKey = ed25519PublicKey;
    window.owsSha512 = sha512;
})();
