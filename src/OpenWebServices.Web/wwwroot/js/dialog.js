// Opens and closes native <dialog> elements for ConfirmDialog. showModal() gives the focus trap, the backdrop and
// Esc handling; Esc while an action runs is ignored so the dialog cannot vanish mid-way.
window.owsDialog = {
    show(dialog) {
        if (!dialog || dialog.open) return;
        dialog.addEventListener("cancel", (e) => {
            if (dialog.querySelector("button:disabled")) e.preventDefault();
        });
        dialog.showModal();
    },
    close(dialog) {
        if (dialog && dialog.open) dialog.close();
    },
};
