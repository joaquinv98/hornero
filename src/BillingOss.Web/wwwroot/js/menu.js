// Menú clásico (sección 10 del diseño de interfaz): cada área es un <details class="bo-menu">, que se abre con clic,
// toque o teclado sin este archivo. Esto solo lo cierra al hacer clic afuera o con Escape.
document.addEventListener('click', (e) => {
    for (const d of document.querySelectorAll('details.bo-menu[open]')) {
        if (!d.contains(e.target)) d.open = false;
    }
});
document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    for (const d of document.querySelectorAll('details.bo-menu[open]')) {
        d.open = false;
        d.querySelector('summary').focus();
    }
});
