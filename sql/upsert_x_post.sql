INSERT INTO x_posts (
    post_id,
    author_handle,
    author_display_name,
    post_text,
    published_at_utc,
    post_url,
    is_text_truncated,
    first_saved_at_utc,
    last_saved_at_utc
) VALUES (
    @PostId,
    @AuthorHandle,
    @AuthorDisplayName,
    @PostText,
    @PublishedAtUtc,
    @PostUrl,
    @IsTextTruncated,
    UTC_TIMESTAMP(3),
    UTC_TIMESTAMP(3)
) AS incoming
ON DUPLICATE KEY UPDATE
    author_handle = incoming.author_handle,
    author_display_name = COALESCE(NULLIF(incoming.author_display_name, ''), x_posts.author_display_name),
    post_url = incoming.post_url,
    -- Update the flag before the text so this expression checks the existing text.
    is_text_truncated = CASE
        WHEN CHAR_LENGTH(incoming.post_text) > 0
            AND (CHAR_LENGTH(x_posts.post_text) = 0 OR NOT incoming.is_text_truncated)
            THEN incoming.is_text_truncated
        ELSE x_posts.is_text_truncated
    END,
    post_text = CASE
        WHEN CHAR_LENGTH(incoming.post_text) > 0
            AND (
                NOT incoming.is_text_truncated
                OR (
                    x_posts.is_text_truncated
                    AND CHAR_LENGTH(incoming.post_text) > CHAR_LENGTH(x_posts.post_text)
                )
            )
            THEN incoming.post_text
        ELSE x_posts.post_text
    END,
    last_saved_at_utc = GREATEST(x_posts.last_saved_at_utc, incoming.last_saved_at_utc);
