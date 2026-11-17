SELECT
    post_id,
    author_handle,
    author_display_name,
    post_text,
    published_at_utc,
    post_url,
    is_text_truncated,
    first_saved_at_utc,
    last_saved_at_utc
FROM x_posts
WHERE author_handle = @AuthorHandle
  AND published_at_utc >= @StartUtc
  AND published_at_utc < @EndUtc
ORDER BY published_at_utc DESC, post_id DESC;
