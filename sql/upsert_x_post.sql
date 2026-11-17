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
)
ON DUPLICATE KEY UPDATE
    author_handle = @AuthorHandle,
    author_display_name = COALESCE(NULLIF(@AuthorDisplayName, ''), x_posts.author_display_name),
    post_url = @PostUrl,
    -- Update the flag before the text so this expression checks the existing text.
    is_text_truncated = CASE
        WHEN CHAR_LENGTH(@PostText) > 0
            AND (CHAR_LENGTH(x_posts.post_text) = 0 OR NOT @IsTextTruncated)
            THEN @IsTextTruncated
        ELSE x_posts.is_text_truncated
    END,
    post_text = CASE
        WHEN CHAR_LENGTH(@PostText) > 0
            AND (
                NOT @IsTextTruncated
                OR CHAR_LENGTH(x_posts.post_text) = 0
                OR (
                    x_posts.is_text_truncated
                    AND CHAR_LENGTH(@PostText) > CHAR_LENGTH(x_posts.post_text)
                )
            )
            THEN @PostText
        ELSE x_posts.post_text
    END,
    last_saved_at_utc = GREATEST(x_posts.last_saved_at_utc, UTC_TIMESTAMP(3));
