// Akiba: the authenticator-code boxes on the second sign-in step.
//
// Progressive enhancement over an ordinary form. Without this file the page is a text field and
// a Continue button that post to /auth/code, and that still works. With it, the one real input
// is drawn as six boxes, the sixth digit submits by itself, and the page shows the code being
// checked and accepted before it moves on.
//
// It reads the code field and nothing else. It writes digits the official typed into boxes with
// textContent - never HTML - and it posts the form to its own action with the form's own
// antiforgery token, so the server sees exactly the request the Continue button would send.
//
// Loaded once for the whole site (App.razor) and driven by listeners on the document rather
// than on elements. Blazor replaces the prerendered page when its circuit connects; listeners
// attached to the old elements would silently stop working, and anything already typed would
// vanish with them. Watching the document for the field instead survives the swap.

(() => {
    const LENGTH = 6;

    // How long the verifying state is shown at the least, and how long the tick is shown before
    // moving on. Long enough to read as a response to what the official did; short enough that
    // four people signing in every morning never wait on an animation.
    const VERIFYING_AT_LEAST_MS = 900;
    const SUCCESS_HOLD_MS = 950;
    const REJECTED_HOLD_MS = 450;

    let carried = '';   // digits typed before Blazor swapped the page in
    let busy = false;

    const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const wait = ms => new Promise(resolve => window.setTimeout(resolve, ms));
    const digitsOf = value => (value || '').replace(/\D/g, '').slice(0, LENGTH);

    function partsOf(element) {
        const root = element.closest('[data-akiba-otp]');
        if (!root) {
            return null;
        }

        const form = root.closest('form');

        return {
            root,
            form,
            input: root.querySelector('.akiba-otp-input'),
            boxes: [...root.querySelectorAll('.akiba-otp-box')],
            status: form && form.querySelector('[data-akiba-otp-status]'),
            button: form && form.querySelector('button[type="submit"]'),
        };
    }

    function render(parts) {
        const value = parts.input.value;
        const focused = document.activeElement === parts.input;
        const caret = focused ? Math.min(parts.input.selectionStart ?? value.length, LENGTH - 1) : -1;

        parts.boxes.forEach((box, index) => {
            box.textContent = value[index] ?? '';
            box.classList.toggle('is-filled', index < value.length);
            box.classList.toggle('is-active', index === caret && !busy);
        });
    }

    function enhance(root) {
        if (root.dataset.enhanced) {
            return;
        }

        root.dataset.enhanced = 'true';
        root.classList.add('is-enhanced');

        const parts = partsOf(root);

        // The markup allows seven characters so that "123 456" can be pasted without the
        // script. With it, everything but digits is dropped as it arrives, so the limit would
        // only get in the way of a pasted code with a space or a dash in it.
        parts.input.removeAttribute('maxlength');

        if (carried && !parts.input.value) {
            parts.input.value = carried;
        }

        // autofocus only applies to the page as first delivered. After Blazor swaps the page
        // in, the field it focused no longer exists and nothing has focus - which on this
        // screen means typing goes nowhere until somebody clicks.
        if (document.activeElement === document.body || document.activeElement === null) {
            parts.input.focus({ preventScroll: true });
        }

        render(parts);
    }

    function enhanceAll() {
        document.querySelectorAll('[data-akiba-otp]').forEach(enhance);
    }

    function setState(parts, state, message, buttonText, tone) {
        parts.root.classList.remove('is-verifying', 'is-verified', 'is-rejected');

        if (state) {
            parts.root.classList.add(state);
        }

        if (parts.status) {
            parts.status.textContent = message;
            parts.status.dataset.tone = tone || '';
        }

        if (parts.button) {
            const label = parts.button.querySelector('.mud-button-label') || parts.button;
            label.textContent = buttonText;
            parts.button.disabled = state === 'is-verifying' || state === 'is-verified';
        }

        parts.input.readOnly = state === 'is-verifying' || state === 'is-verified';
        render(parts);
    }

    function reset(parts, message) {
        busy = false;
        setState(parts, null, message || '', 'Continue', message ? 'error' : '');
        parts.input.focus();
        parts.input.select();
    }

    async function verify(parts) {
        if (busy) {
            return;
        }

        const code = digitsOf(parts.input.value);

        if (code.length !== LENGTH) {
            setState(parts, null, 'Enter all six digits of the code.', 'Continue', 'error');
            parts.input.focus();
            return;
        }

        busy = true;
        parts.input.value = code;

        const motion = !reducedMotion();
        setState(parts, 'is-verifying', 'Checking your code…', 'Verifying…');

        let response;

        try {
            [response] = await Promise.all([
                fetch(parts.form.action, {
                    method: 'POST',
                    body: new FormData(parts.form),
                    credentials: 'same-origin',
                    redirect: 'follow',
                }),
                wait(motion ? VERIFYING_AT_LEAST_MS : 0),
            ]);
        } catch {
            // The connection failed before the server answered. Hand the form to the browser
            // the ordinary way, which at least shows the official what went wrong.
            HTMLFormElement.prototype.submit.call(parts.form);
            return;
        }

        if (response.status === 429) {
            reset(parts, 'Too many attempts. Wait a minute, then try the current code.');
            return;
        }

        // Every outcome of /auth/code is a redirect. Anything else is not a verdict on the code,
        // and is not treated as one.
        if (!response.ok || !response.redirected) {
            reset(parts, 'Something went wrong checking the code. Try again.');
            return;
        }

        const destination = new URL(response.url, window.location.origin);

        // A rejected code, a lockout and an expired page all send the official back to one of
        // the sign-in screens; only an accepted code sends them anywhere else. The server's
        // own page then says which it was.
        if (destination.pathname.startsWith('/sign-in')) {
            setState(parts, 'is-rejected', 'That code was not accepted.', 'Continue', 'error');
            await wait(motion ? REJECTED_HOLD_MS : 0);
            window.location.assign(destination.href);
            return;
        }

        setState(parts, 'is-verified', 'Verified. Signing you in…', 'Verified');
        await wait(motion ? SUCCESS_HOLD_MS : 350);
        window.location.assign(destination.href);
    }

    document.addEventListener('input', event => {
        if (!event.target.matches || !event.target.matches('.akiba-otp-input')) {
            return;
        }

        const parts = partsOf(event.target);
        const digits = digitsOf(parts.input.value);

        if (parts.input.value !== digits) {
            parts.input.value = digits;
        }

        carried = digits;
        render(parts);

        if (digits.length === LENGTH) {
            verify(parts);
        }
    });

    // The active box follows the caret, so it has to be redrawn whenever the caret can move.
    for (const type of ['keyup', 'click', 'focusin', 'focusout', 'select']) {
        document.addEventListener(type, event => {
            if (event.target.matches && event.target.matches('.akiba-otp-input')) {
                render(partsOf(event.target));
            }
        });
    }

    // The Continue button and Enter both come through here, so every way of submitting gets
    // the same checking and the same feedback. Capture, so this runs before anything else can
    // act on the submission.
    document.addEventListener('submit', event => {
        const form = event.target;
        const root = form.querySelector && form.querySelector('[data-akiba-otp].is-enhanced');

        if (!root) {
            return;
        }

        event.preventDefault();
        verify(partsOf(root));
    }, true);

    // Coming back with the Back button restores the page as it was left, mid-verification.
    window.addEventListener('pageshow', event => {
        if (event.persisted) {
            document.querySelectorAll('[data-akiba-otp].is-enhanced')
                .forEach(root => reset(partsOf(root)));
        }
    });

    new MutationObserver(enhanceAll).observe(document.documentElement, { childList: true, subtree: true });

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', enhanceAll);
    } else {
        enhanceAll();
    }
})();
