(() => {
    const xHosts = new Set(["x.com", "www.x.com", "twitter.com", "www.twitter.com"]);
    if (location.protocol !== "https:" || !xHosts.has(location.hostname)) {
        return null;
    }

    const timeline = document.querySelector('[data-testid="primaryColumn"]') ??
        document.querySelector('main, [role="main"]') ?? document.body;
    const markedPost = '[data-testid="tweet"]';
    const article = 'article, [role="article"]';
    const cell = '[data-testid="cellInnerDiv"]';
    const headerSelector = '[data-testid="User-Name"], [data-testid="UserName"]';
    const quoteSelector = '[data-testid="quoteTweet"], [data-testid="quotedTweet"]';
    const statusLink = 'a[href*="/status/"]';
    const viewportWidth = document.documentElement.clientWidth || window.innerWidth;
    const viewportHeight = document.documentElement.clientHeight || window.innerHeight;
    const posts = new Map();

    function visibleInViewport(card) {
        const bounds = card.getBoundingClientRect();
        const style = getComputedStyle(card);
        return bounds.width > 0 && bounds.height > 0 && bounds.right > 0 && bounds.bottom > 0 &&
            bounds.left < viewportWidth && bounds.top < viewportHeight && style.visibility === "visible" &&
            style.opacity !== "0";
    }

    function findOwnHeader(card) {
        return Array.from(card.querySelectorAll(headerSelector))
            .find(header => !header.closest(quoteSelector));
    }

    function belongsToPost(element, card, header) {
        const quote = element.closest(quoteSelector);
        if (quote && card.contains(quote)) {
            return false;
        }

        const nestedCard = element.closest(markedPost + ", " + article);
        if (nestedCard && nestedCard !== card && card.contains(nestedCard)) {
            return false;
        }

        // A native a[role=link] can be the permalink itself. A separate linked container
        // inside the card that does not contain its header is a quote or another embedded card.
        const embeddedLink = element.closest('[role="link"]:not(a)');
        return !header || !embeddedLink || embeddedLink === card || !card.contains(embeddedLink) ||
            embeddedLink.contains(header);
    }

    function readText(element) {
        if (!element.querySelector("img[alt]")) {
            return element.innerText;
        }
        return Array.from(element.childNodes, node => {
            if (node.nodeType === Node.TEXT_NODE) return node.textContent;
            if (node instanceof HTMLImageElement) return node.alt;
            if (node instanceof HTMLBRElement) return "\n";
            return node instanceof HTMLElement ? readText(node) : "";
        }).join("");
    }

    function parseStatus(link) {
        if (!link || !xHosts.has(link.hostname)) {
            return null;
        }
        const generic = /^\/i\/(?:web\/)?status\/(\d+)\/?$/i.exec(link.pathname);
        if (generic) {
            return { id: generic[1], handle: null };
        }
        const canonical = /^\/([a-z0-9_]{1,15})\/status\/(\d+)\/?$/i.exec(link.pathname);
        return canonical && { id: canonical[2], handle: canonical[1] };
    }

    function publishedTime(link, cardTime, id) {
        const machineTime = link.querySelector("time");
        const value = machineTime?.getAttribute("datetime") ?? cardTime?.getAttribute("datetime");
        if (value && /(?:Z|[+-]\d\d:\d\d)$/i.test(value)) {
            const parsed = Date.parse(value);
            if (Number.isFinite(parsed)) {
                return parsed;
            }
        }

        // Contemporary X post IDs encode the creation time. Legacy short IDs do not.
        if (/^\d{17,19}$/.test(id)) {
            const milliseconds = Number((BigInt(id) >> 22n) + 1288834974657n);
            if (milliseconds <= Date.now() + 86400000) {
                return milliseconds;
            }
        }
        return null;
    }

    function mediaUrl(source, host, pathPattern) {
        try {
            const url = new URL(source);
            if (url.protocol !== "https:" || url.hostname !== host || url.port ||
                url.username || url.password || !pathPattern.test(url.pathname)) return null;
            url.hash = "";
            return url.href;
        } catch {
            return null;
        }
    }

    function readAttachments(card, header, postUrl) {
        const attachments = [];
        const seenPhotos = new Set();
        let photoPosition = 0;
        let videoPosition = 0;
        const playerSelector = '[data-testid="videoPlayer"], [data-testid="videoComponent"]';
        for (const element of card.querySelectorAll('[data-testid="tweetPhoto"] img, video, ' + playerSelector)) {
            if (!belongsToPost(element, card, header)) continue;

            if (element instanceof HTMLImageElement) {
                // Video overlay thumbnails are handled as posters below.
                if (element.closest(playerSelector)) continue;
                const url = mediaUrl(element.currentSrc || element.src, "pbs.twimg.com", /^\/media\//);
                if (!url) continue;
                const identity = new URL(url).pathname;
                if (seenPhotos.has(identity)) continue;
                seenPhotos.add(identity);

                const linked = element.closest("a[href]")?.href;
                const slot = linked?.match(/\/photo\/(\d+)(?:[?#]|$)/)?.[1];
                const position = Number(slot) > 0 && Number(slot) <= 20 ? Number(slot) : photoPosition + 1;
                photoPosition = Math.max(photoPosition, position);
                attachments.push({
                    position, kind: "photo", linkUrl: `${postUrl}/photo/${position}`,
                    mediaUrl: url, posterUrl: null, altText: element.getAttribute("alt")
                });
                continue;
            }

            const isVideo = element instanceof HTMLVideoElement;
            const player = element.closest(playerSelector);
            if (!isVideo && (element.querySelector("video") || element.parentElement.closest(playerSelector))) continue;
            const video = [element.currentSrc, element.getAttribute("src"),
                ...Array.from(element.querySelectorAll("source[src]"), source => source.src)]
                .map(source => mediaUrl(source, "video.twimg.com", /\.mp4$/i)).find(Boolean) ?? null;
            const poster = [element.getAttribute("poster"),
                ...Array.from(player?.querySelectorAll("img[src]") ?? [], image => image.currentSrc || image.src)]
                .map(source => mediaUrl(source, "pbs.twimg.com",
                    /^\/(?:ext_tw_video_thumb|amplify_video_thumb|tweet_video_thumb|media)\//)).find(Boolean) ?? null;
            if (!isVideo && !video && !poster) continue;
            const position = ++videoPosition;
            attachments.push({
                position, kind: "video", linkUrl: `${postUrl}/video/${position}`,
                mediaUrl: video, posterUrl: poster, altText: null
            });
        }
        return attachments;
    }

    const candidates = new Set();
    for (const node of timeline.querySelectorAll(markedPost + ", " + article + ", " + cell)) {
        if (node.matches(markedPost) || findOwnHeader(node) && node.querySelector(statusLink)) {
            candidates.add(node);
        }
    }

    let visibleCards = 0;
    let unreadableCards = 0;
    for (const card of candidates) {
        if (card.closest(quoteSelector) || !visibleInViewport(card)) {
            continue;
        }
        if (card.matches(cell) && Array.from(card.querySelectorAll(markedPost + ", " + article))
            .some(child => candidates.has(child))) {
            continue;
        }
        const parentCard = card.parentElement.closest(markedPost + ", " + article);
        if (candidates.has(parentCard)) {
            continue;
        }

        visibleCards++;
        const header = findOwnHeader(card);
        const ownLinks = Array.from(card.querySelectorAll(statusLink))
            .filter(link => belongsToPost(link, card, header));
        const headerLinks = ownLinks.filter(link => header?.contains(link));
        const permalink = headerLinks.find(link => parseStatus(link) && link.querySelector("time")) ??
            headerLinks.find(link => parseStatus(link)) ??
            ownLinks.find(link => parseStatus(link) && link.querySelector("time")) ??
            ownLinks.find(link => parseStatus(link));
        const address = parseStatus(permalink);
        const authorLinks = header ? Array.from(header.querySelectorAll('a[href]')).filter(
            link => xHosts.has(link.hostname) && /^\/[a-z0-9_]{1,15}\/?$/i.test(link.pathname)) : [];
        const authorHandle = address?.handle ?? authorLinks[0]?.pathname.split("/")[1];
        const cardTime = header?.querySelector("time[datetime]");
        const timestamp = address && publishedTime(permalink, cardTime, address.id);
        if (!address || !authorHandle || timestamp === null) {
            unreadableCards++;
            continue;
        }
        if (posts.has(address.id)) {
            continue;
        }

        const authorLink = authorLinks.find(link =>
            link.pathname.split("/")[1].toLowerCase() === authorHandle.toLowerCase());
        const body = Array.from(card.querySelectorAll('[data-testid="tweetText"]'))
            .find(element => belongsToPost(element, card, header));
        const showMore = Array.from(card.querySelectorAll('[data-testid="tweet-text-show-more-link"]'))
            .some(element => belongsToPost(element, card, header));
        const postUrl = "https://x.com/" + authorHandle + "/status/" + address.id;
        posts.set(address.id, {
            id: address.id,
            authorHandle,
            authorDisplayName: authorLink ? readText(authorLink).trim() : null,
            text: body ? readText(body).trim() : "",
            publishedAtUtc: new Date(timestamp).toISOString(),
            url: postUrl,
            isTextTruncated: showMore,
            attachments: readAttachments(card, header, postUrl)
        });
    }

    return { visibleCards, unreadableCards, posts: Array.from(posts.values()) };
})()
