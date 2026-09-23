// Akiba: the keyboard, and the idle clock.
//
// The only JavaScript in the application, and deliberately the smallest thing that could
// work. It reads no data, writes no HTML, and takes no argument from the page. Everything it
// decides, it hands back to .NET to act on.
//
// Two jobs, both of which exist because the browser knows something the server cannot.
//
//   The keyboard. The privacy blur is useless if reaching it means finding the mouse while
//   somebody is already walking towards the desk, so Ctrl+Shift+H does it with a hand that is
//   already on the keyboard.
//
//   The idle clock. The server sees a circuit that is open, not an official who is present.
//   Only the browser can tell the difference between somebody reading a long report and
//   somebody who went to lunch with the members' register on screen.

let keyHandler = null;
let activityHandler = null;
let timer = null;
let owner = null;

// Hardcoded rather than passed in. These are a security decision, and a security decision that
// the page can talk the browser out of is not one.
const IDLE_MS = 5 * 60 * 1000;
const WARNING_MS = 30 * 1000;

// What counts as somebody being here. Deliberately physical events only: a re-render, a
// background poll or a timer firing is the application being busy, not a person being present.
const ACTIVITY = ['pointerdown', 'keydown', 'wheel', 'touchstart'];

export function register(dotNetOwner) {
    unregister();

    owner = dotNetOwner;

    keyHandler = (event) => {
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

    document.addEventListener('keydown', keyHandler);

    // Passive: these listeners never call preventDefault, and saying so keeps scrolling smooth
    // on a long journal.
    activityHandler = () => restart();

    for (const event of ACTIVITY) {
        document.addEventListener(event, activityHandler, { passive: true });
    }

    restart();
}

// The clock runs in two stages so the official is warned rather than simply dropped. Being
// thrown back to the sign-in screen mid-entry, with no idea whether the receipt was saved, is
// how people learn to leave a second browser open to avoid it.
function restart() {
    clear();

    timer = window.setTimeout(() => {
        owner?.invokeMethodAsync('WarnSessionEndingAsync', Math.round(WARNING_MS / 1000));

        // Not restarted by activity. Past this point the official has to say they are here -
        // the dialog's own button does that - because a stray scroll from a sleeve on the desk
        // is not somebody being present.
        timer = window.setTimeout(() => signOut(), WARNING_MS);
    }, IDLE_MS - WARNING_MS);
}

function clear() {
    if (timer) {
        window.clearTimeout(timer);
        timer = null;
    }
}

/// Called from .NET when the official answers the warning.
export function staySignedIn() {
    restart();
}

// Submits the sign-out form the layout already renders, rather than navigating anywhere. That
// keeps the whole thing a POST carrying its antiforgery token, so the timeout goes out through
// exactly the endpoint the sign-out button uses and nothing here needs a weaker one. This
// reads an element; it writes none.
function signOut() {
    clear();

    const form = document.querySelector('form[data-akiba-timed-out]');

    if (form) {
        form.requestSubmit();
    } else {
        // The form is gone, so something is already navigating. Reloading lands on the sign-in
        // screen if the cookie has expired, which is the outcome we wanted anyway.
        window.location.reload();
    }
}

export function unregister() {
    if (keyHandler) {
        document.removeEventListener('keydown', keyHandler);
        keyHandler = null;
    }

    if (activityHandler) {
        for (const event of ACTIVITY) {
            document.removeEventListener(event, activityHandler);
        }

        activityHandler = null;
    }

    clear();
    owner = null;
}
