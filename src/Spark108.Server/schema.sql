CREATE TABLE IF NOT EXISTS write_gate (
    id TINYINT PRIMARY KEY
) ENGINE=InnoDB;
INSERT IGNORE INTO write_gate (id) VALUES (1);

CREATE TABLE IF NOT EXISTS users (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    subject VARCHAR(255) COLLATE utf8mb4_bin NOT NULL UNIQUE,
    display_name VARCHAR(255) NOT NULL,
    email VARCHAR(255) NOT NULL,
    is_admin BOOLEAN NOT NULL DEFAULT FALSE,
    enabled BOOLEAN NOT NULL DEFAULT TRUE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS sessions (
    token_hash BINARY(32) PRIMARY KEY,
    user_id BIGINT NOT NULL,
    purpose VARCHAR(12) NOT NULL,
    expires_at DATETIME NOT NULL,
    INDEX (expires_at),
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS desktop_codes (
    code_hash BINARY(32) PRIMARY KEY,
    user_id BIGINT NOT NULL,
    challenge VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    expires_at DATETIME NOT NULL,
    INDEX (expires_at),
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS apps (
    id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
    name VARCHAR(120) NOT NULL,
    description TEXT NOT NULL,
    kind INT NOT NULL,
    restricted BOOLEAN NOT NULL DEFAULT FALSE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS app_access (
    app_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    user_id BIGINT NOT NULL,
    PRIMARY KEY(app_id,user_id),
    FOREIGN KEY(app_id) REFERENCES apps(id) ON DELETE CASCADE,
    FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS app_dependencies (
    app_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    dependency_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PRIMARY KEY(app_id,dependency_id),
    FOREIGN KEY(app_id) REFERENCES apps(id) ON DELETE CASCADE,
    FOREIGN KEY(dependency_id) REFERENCES apps(id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS releases (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    app_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    version VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    major INT NOT NULL,
    minor INT NOT NULL,
    patch INT NOT NULL,
    notes TEXT NOT NULL,
    entry_point VARCHAR(240) NULL,
    size_bytes BIGINT NOT NULL,
    sha256 CHAR(64) CHARACTER SET ascii NOT NULL,
    storage_key CHAR(32) CHARACTER SET ascii NOT NULL,
    published BOOLEAN NOT NULL DEFAULT FALSE,
    UNIQUE (app_id,version),
    INDEX (app_id,published,major,minor,patch),
    FOREIGN KEY (app_id) REFERENCES apps(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT IGNORE INTO apps (id,name,description,kind,restricted)
VALUES ('spark108-app-center','Spark108 App Center','Центр приложений для инфраструктуры Spark108. Обновляет клиент и управляет корпоративными программами и их компонентами.',2,FALSE);
