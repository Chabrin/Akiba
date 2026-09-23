// Akiba keyboard shortcuts.
//
// The only JavaScript in the application, and deliberately the smallest thing that could
// work: it reads no data, writes no HTML, and touches nothing but the keyboard.
//
// It exists for one reason. The privacy blur is useless if reaching it means finding the
// mouse while somebody is already walking towards the desk, so Ctrl+Shift+H does it with a
// hand that is already on the keyboard.

let handler = null;

export function register(owner) {
    unregister();

    handler = (event) => {
        if (!event.ctrlKey || !event.shiftKey || event.altKey) {
            return;
        }

        // Compared on `code`, not `key`. With Shift held, `key` is the shifted character and
        // depends on the keyboard layout; `code` is the physical key and does not.
        if (event.code !== 'KeyH') {
            return;
        }

        event.preventDefault();
        owner.invokeMethodAsync('TogglePrivacyFromKeyboard');
    };

    document.addEventListener('keydown', handler);
}

export function unregister() {
    if (handler) {
        document.removeEventListener('keydown', handler);
        handler = null;
    }
}
