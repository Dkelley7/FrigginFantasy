const invitation = document.getElementById('invite-url');
if (invitation) {
    // The browser knows the public HTTPS origin, even behind a local proxy.
    invitation.value = new URL(invitation.value, window.location.origin).href;
    invitation.addEventListener('focus', () => invitation.select());
    document.getElementById('copy-invite')?.addEventListener('click', async () => {
        const status = document.getElementById('copy-status');
        try {
            await navigator.clipboard.writeText(invitation.value);
            status.textContent = 'Invitation copied.';
        } catch {
            invitation.focus();
            invitation.select();
            status.textContent = 'Select and copy the link above.';
        }
    });
}
