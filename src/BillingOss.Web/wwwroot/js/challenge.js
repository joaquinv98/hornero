// Desafío anti robots propio (diseno/ingreso-clientes.md, sección 7). Antes de mandar el formulario busca un número que,
// con el desafío firmado por el servidor, dé un SHA-256 con los primeros bits en cero. No le pide nada a la persona.
(() => {
    const zeros = (bytes, bits) => {
        for (let i = 0; i < bits; i++) {
            if (bytes[i >> 3] & (0x80 >> (i & 7))) {
                return false;
            }
        }
        return true;
    };

    document.querySelectorAll('input[name="Challenge"][data-bits]').forEach(challenge => {
        const form = challenge.form;
        const nonce = form.querySelector('input[name="ChallengeNonce"]');
        form.addEventListener('submit', async event => {
            if (nonce.value) {
                return;
            }

            event.preventDefault();
            const button = event.submitter;
            if (button) {
                button.disabled = true;
            }

            const bits = Number(challenge.dataset.bits);
            const encoder = new TextEncoder();
            for (let n = 0; ; n++) {
                const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(challenge.value + ':' + n)));
                if (zeros(hash, bits)) {
                    nonce.value = String(n);
                    break;
                }
            }

            if (button) {
                button.disabled = false;
            }

            form.requestSubmit(button);
        });
    });
})();
