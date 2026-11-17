(() => {
    const xHosts = new Set(["x.com", "www.x.com", "twitter.com", "www.twitter.com"]);
    if (location.protocol !== "https:" || !xHosts.has(location.hostname)) {
        return null;
    }

    const timeline = document.querySelector('[data-testid="primaryColumn"]') ??
        document.querySelector('main, [role="main"]');
    if (!timeline) {
        return [];
    }

    const postSelector = 'article[data-testid="tweet"]';
    const quoteSelector = '[role="link"], [data-testid="quoteTweet"], [data-testid="quotedTweet"]';
    const viewportWidth = document.documentElement.clientWidth;
    const viewportHeight = document.documentElement.clientHeight;
    const posts = new Map();

    function visibleInViewport(article) {
        if (!article.checkVisibility({ opacityProperty: true, visibilityProperty: true })) {
            return false;
        }
        const bounds = article.getBoundingClientRect();
        return bounds.width > 0 && bounds.height > 0 && bounds.right > 0 && bounds.bottom > 0 &&
            bounds.left < viewportWidth && bounds.top < viewportHeight;
    }

    function belongsToPost(element, article) {
        const quote = element.closest(quoteSelector);
        return element.closest(postSelector) === article && (!quote || !article.contains(quote));
    }

    function findInPost(article, selector) {
        return Array.from(article.querySelectorAll(selector)).find(element => belongsToPost(element, article));
    }

    function readText(element) {
        if (!element.querySelector("img[alt]")) {
            return element.innerText;
        }
        return Array.from(element.childNodes, node => {
            if (node.nodeType === Node.TEXT_NODE) {
                return node.textContent;
            }
            if (node instanceof HTMLImageElement) {
                return node.alt;
            }
            if (node instanceof HTMLBRElement) {
                return "\n";
            }
            return node instanceof HTMLElement ? readText(node) : "";
        }).join("");
    }

    for (const article of timeline.querySelectorAll(postSelector)) {
        if (article.parentElement.closest(postSelector) || !visibleInViewport(article)) {
            continue;
        }

        const timestamp = findInPost(article, '[data-testid="User-Name"] time[datetime]') ??
            findInPost(article, 'time[datetime]');
        const permalink = timestamp?.closest("a[href]");
        if (!permalink) {
            continue;
        }

        const address = new URL(permalink.href);
        const match = /^\/([a-z0-9_]{1,15})\/status\/([0-9]+)\/?$/i.exec(address.pathname);
        const publishedAt = Date.parse(timestamp.dateTime);
        if (!xHosts.has(address.hostname) || !match || !Number.isFinite(publishedAt)) {
            continue;
        }

        const [, authorHandle, id] = match;
        if (posts.has(id)) {
            continue;
        }

        const author = findInPost(article, '[data-testid="User-Name"]');
        const authorLink = author && Array.from(author.querySelectorAll("a[href]")).find(
            link => new URL(link.href).pathname.toLowerCase() === "/" + authorHandle.toLowerCase());
        const body = findInPost(article, '[data-testid="tweetText"]');
        const showMore = findInPost(article, '[data-testid="tweet-text-show-more-link"]');
        posts.set(id, {
            id,
            authorHandle,
            authorDisplayName: authorLink ? readText(authorLink).trim() : null,
            text: body ? readText(body).trim() : "",
            publishedAtUtc: new Date(publishedAt).toISOString(),
            url: 'https://x.com/' + authorHandle + '/status/' + id,
            isTextTruncated: Boolean(showMore)
        });
    }

    return Array.from(posts.values());
})()
