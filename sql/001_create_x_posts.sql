CREATE TABLE IF NOT EXISTS x_posts (
    post_id VARCHAR(20) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    author_handle VARCHAR(15) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL,
    author_display_name VARCHAR(255) NULL,
    post_text MEDIUMTEXT NOT NULL,
    published_at_utc DATETIME(3) NOT NULL,
    post_url VARCHAR(255) NOT NULL,
    is_text_truncated BOOLEAN NOT NULL,
    first_saved_at_utc DATETIME(3) NOT NULL,
    last_saved_at_utc DATETIME(3) NOT NULL,
    PRIMARY KEY (post_id),
    KEY ix_x_posts_author_published (author_handle, published_at_utc, post_id),
    CONSTRAINT chk_x_posts_text_truncated CHECK (is_text_truncated IN (0, 1))
) ENGINE = InnoDB
  DEFAULT CHARACTER SET = utf8mb4
  COLLATE = utf8mb4_unicode_ci;
