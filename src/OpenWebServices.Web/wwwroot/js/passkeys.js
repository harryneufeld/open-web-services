// Passkey sign-in and registration for forms marked data-passkey="login" or data-passkey="register".
// The form posts its antiforgery token to data-options-url, hands the options to the browser's WebAuthn API,
// puts the resulting credential (JSON) into the form's "credential" field and submits the form normally.
(() => {
    const supported = () =>
        typeof window.PublicKeyCredential === "function" &&
        typeof PublicKeyCredential.parseCreationOptionsFromJSON === "function" &&
        typeof PublicKeyCredential.parseRequestOptionsFromJSON === "function";

    const say = (form, text) => {
        const status = form.querySelector("[data-passkey-status]");
        if (status) status.textContent = text;
    };

    async function run(form) {
        if (!supported()) {
            say(form, window.isSecureContext
                ? "This browser does not support passkeys."
                : "Passkeys need HTTPS. Open ows over https:// (or localhost) to use them.");
            return;
        }

        const body = new FormData();
        body.append("__RequestVerificationToken", form.querySelector("input[name='__RequestVerificationToken']").value);
        const response = await fetch(form.dataset.optionsUrl, { method: "POST", body, credentials: "same-origin" });
        if (!response.ok) {
            say(form, response.status === 403
                ? "Sign in with your authenticator code or a passkey first."
                : "Could not start the passkey request. Reload the page and try again.");
            return;
        }

        const options = await response.json();
        try {
            const credential = form.dataset.passkey === "register"
                ? await navigator.credentials.create({ publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(options) })
                : await navigator.credentials.get({ publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(options) });
            form.querySelector("input[name='credential']").value = JSON.stringify(credential);
            form.submit();
        } catch (error) {
            say(form, error.name === "NotAllowedError"
                ? "The passkey request was cancelled or timed out."
                : `Passkey error: ${error.message}`);
        }
    }

    document.addEventListener("submit", event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.dataset.passkey) return;
        event.preventDefault();
        say(form, "");
        run(form).catch(error => say(form, `Passkey error: ${error.message}`));
    });
})();
