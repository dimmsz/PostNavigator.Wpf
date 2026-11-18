(() => {
    const readVisiblePosts = () => __READ_VISIBLE_POSTS__;
    const sessionId = __SESSION_ID__;
    if (!window.__postNavigatorCapture) {
        let activeSession = null;
        let timer = null;
        let observer = null;

        function emit() {
            timer = null;
            if (!activeSession) return;
            const capture = readVisiblePosts();
            if (capture?.posts?.length) {
                window.chrome.webview.postMessage({
                    kind: "postNavigatorCapture",
                    sessionId: activeSession,
                    capture
                });
            }
        }

        function schedule() {
            if (timer === null) timer = setTimeout(emit, 100);
        }

        window.__postNavigatorCapture = {
            configure(id) {
                activeSession = id;
                if (!observer) {
                    observer = new MutationObserver(schedule);
                    observer.observe(document.documentElement, {
                        childList: true, subtree: true, characterData: true,
                        attributes: true, attributeFilter: ["datetime", "href"]
                    });
                    document.addEventListener("scroll", schedule, { capture: true, passive: true });
                    window.addEventListener("resize", schedule, { passive: true });
                }
            },
            stop(id) {
                if (activeSession !== id) return;
                activeSession = null;
                observer?.disconnect();
                observer = null;
                document.removeEventListener("scroll", schedule, true);
                window.removeEventListener("resize", schedule);
                if (timer !== null) clearTimeout(timer);
                timer = null;
            }
        };
    }

    window.__postNavigatorCapture.configure(sessionId);
    return readVisiblePosts();
})()
