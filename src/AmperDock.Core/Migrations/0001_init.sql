-- AmperDock local settings database (SQLite 3.38+), migration 0001.
-- File: %APPDATA%\AmperDock\amperdock.db. There is one database per Windows user profile.
-- Access rules: there is no owner column, because the database lives in the user's own
-- profile folder and the NTFS ACLs on it are the access control. There is no server.
-- Every write goes through one SettingsStore class (a single writer on a background thread).
--
-- Conventions:
--   ids are UUIDs stored as lowercase TEXT (Guid.NewGuid().ToString())
--   created_at / updated_at are TEXT ISO-8601 UTC ('2026-11-19T07:00:00Z')
--   booleans are INTEGER 0/1 with CHECK constraints
--   style blobs (appearance, effects, behavior) are JSON TEXT, versioned by the app.
--     They change shape too often for columns, and nothing filters on them.
--   schema version: PRAGMA user_version (this file sets it to 1)

PRAGMA foreign_keys = ON;      -- must also be set on every connection by the app
PRAGMA journal_mode = WAL;     -- crash-safe; a dock that dies with Explorer must not corrupt settings

-- Global app settings. A key/value table, so adding a setting needs no migration.
CREATE TABLE settings (
  key         TEXT PRIMARY KEY,
  value_json  TEXT NOT NULL CHECK (json_valid(value_json)),
  updated_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

-- Installed themes. The theme files live on disk in %APPDATA%\AmperDock\themes\<folder>.
CREATE TABLE themes (
  id          TEXT PRIMARY KEY,
  name        TEXT NOT NULL,
  author      TEXT,
  folder      TEXT NOT NULL UNIQUE,
  format      INTEGER NOT NULL DEFAULT 1,
  is_builtin  INTEGER NOT NULL DEFAULT 0 CHECK (is_builtin IN (0,1)),
  created_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  updated_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

-- Container objects: dock, sub-dock, tabs panel (shelf), drawer, stack, launch group.
-- A nested kind is opened from an item: parent_item_id points at that item.
-- Deleting the item deletes the nested container, and with it all of its items, recursively.
CREATE TABLE containers (
  id              TEXT PRIMARY KEY,
  kind            TEXT NOT NULL CHECK (kind IN ('dock','subdock','panel','drawer','stack','launch_group')),
  name            TEXT NOT NULL,
  enabled         INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0,1)),
  parent_item_id  TEXT REFERENCES items(id) ON DELETE CASCADE,
  -- position applies to top-level kinds only
  monitor_key     TEXT,                 -- stable monitor id (device path), not an index
  edge            TEXT CHECK (edge IN ('bottom','top','left','right','float')),
  alignment       REAL CHECK (alignment BETWEEN 0 AND 1),
  offset_px       INTEGER NOT NULL DEFAULT 0,
  float_x         INTEGER,
  float_y         INTEGER,
  span            INTEGER NOT NULL DEFAULT 0 CHECK (span IN (0,1)),
  zorder          TEXT NOT NULL DEFAULT 'normal' CHECK (zorder IN ('normal','top','bottom')),
  reserve_space   INTEGER NOT NULL DEFAULT 0 CHECK (reserve_space IN (0,1)),
  behavior_json   TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(behavior_json)),
  appearance_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(appearance_json)),
  effects_json    TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(effects_json)),
  theme_id        TEXT REFERENCES themes(id) ON DELETE SET NULL,   -- null = inherit the global theme
  folder_path     TEXT,                 -- stacks/drawers that show a live folder
  created_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  updated_at      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  -- nested kinds must have a parent item; top-level kinds must not
  CHECK ((kind IN ('subdock','stack','launch_group')) = (parent_item_id IS NOT NULL)),
  -- nested kinds have no position or behavior (matches the Properties tab subsets in recon)
  CHECK (kind IN ('dock','panel','drawer') OR edge IS NULL),
  CHECK (kind NOT IN ('dock','panel','drawer') OR edge IS NOT NULL)
);
CREATE UNIQUE INDEX containers_one_per_parent_item ON containers(parent_item_id) WHERE parent_item_id IS NOT NULL;
CREATE INDEX containers_theme ON containers(theme_id);
CREATE INDEX containers_kind ON containers(kind, enabled);

-- Items inside a container. sort_order is a REAL so a drop between two items needs one UPDATE
-- (the midpoint). The app renumbers in a transaction when the gap gets too small.
CREATE TABLE items (
  id                    TEXT PRIMARY KEY,
  container_id          TEXT NOT NULL REFERENCES containers(id) ON DELETE CASCADE,
  shelf_tab_id          TEXT REFERENCES shelf_tabs(id) ON DELETE CASCADE,  -- set only for items on a user tab of a panel
  sort_order            REAL NOT NULL,
  type                  TEXT NOT NULL CHECK (type IN ('program','folder','document','url','command','widget','separator','container_link','uwp')),
  label                 TEXT NOT NULL DEFAULT '',
  target                TEXT,            -- path, URL, AUMID (uwp), command id, or widget type
  arguments             TEXT,
  start_in              TEXT,
  hotkey                TEXT,            -- normalised, e.g. 'Ctrl+Alt+K'
  run_state             TEXT NOT NULL DEFAULT 'normal' CHECK (run_state IN ('normal','minimized','maximized')),
  icon_path             TEXT,            -- null = use the target's own icon
  run_as_admin          INTEGER NOT NULL DEFAULT 0 CHECK (run_as_admin IN (0,1)),
  allow_multiple        INTEGER NOT NULL DEFAULT 0 CHECK (allow_multiple IN (0,1)),
  launch_once_for_files INTEGER NOT NULL DEFAULT 1 CHECK (launch_once_for_files IN (0,1)),
  show_folder_as_menu   INTEGER NOT NULL DEFAULT 0 CHECK (show_folder_as_menu IN (0,1)),
  animation_json        TEXT CHECK (animation_json IS NULL OR json_valid(animation_json)),
  created_at            TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  updated_at            TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  CHECK (type IN ('separator','container_link') OR target IS NOT NULL)
);
CREATE INDEX items_container_order ON items(container_id, sort_order);
CREATE INDEX items_tab ON items(shelf_tab_id);
-- a global hotkey can only belong to one item (the Preferences hotkey is checked in the app)
CREATE UNIQUE INDEX items_hotkey ON items(hotkey) WHERE hotkey IS NOT NULL;

-- Tabs of a tabs panel (shelf).
CREATE TABLE shelf_tabs (
  id            TEXT PRIMARY KEY,
  container_id  TEXT NOT NULL REFERENCES containers(id) ON DELETE CASCADE,
  sort_order    REAL NOT NULL,
  type          TEXT NOT NULL CHECK (type IN ('user','desktop','documents','downloads','folder','tasks','recent','recycle','control_panel','themes')),
  label         TEXT NOT NULL,
  folder_path   TEXT,
  sort_by       TEXT NOT NULL DEFAULT 'name' CHECK (sort_by IN ('name','type','created','modified','size')),
  created_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  updated_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  CHECK (type <> 'folder' OR folder_path IS NOT NULL)
);
CREATE INDEX shelf_tabs_container_order ON shelf_tabs(container_id, sort_order);

-- Widget settings, one row per widget item.
-- Secrets (email passwords, OAuth refresh tokens) never go here: they live in Windows Credential
-- Manager under 'AmperDock/<item_id>/<account>'.
CREATE TABLE widget_configs (
  item_id       TEXT PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
  widget_type   TEXT NOT NULL CHECK (widget_type IN ('clock','recycler','cpu','ram','net','email','weather','mascot','battery','calendar','moon','disk')),
  style         TEXT NOT NULL DEFAULT 'default',
  settings_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(settings_json)),
  mute_sounds   INTEGER NOT NULL DEFAULT 0 CHECK (mute_sounds IN (0,1)),
  no_animation  INTEGER NOT NULL DEFAULT 0 CHECK (no_animation IN (0,1)),
  created_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  updated_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX widget_configs_type ON widget_configs(widget_type);

-- Alarms. These are stored as WALL-CLOCK local time plus a time zone, NOT as UTC.
-- "Every day at 07:00" has to stay at 07:00 across a DST change.
-- next_fire_utc is derived, recomputed after every fire, on time-zone change and on resume.
CREATE TABLE alarms (
  id             TEXT PRIMARY KEY,
  label          TEXT NOT NULL DEFAULT '',
  local_date     TEXT,                 -- 'YYYY-MM-DD'; for 'once' and as the anchor for monthly/yearly
  local_time     TEXT NOT NULL CHECK (local_time GLOB '[0-2][0-9]:[0-5][0-9]'),
  time_zone      TEXT NOT NULL,        -- IANA id, e.g. 'America/Denver'
  recurrence     TEXT NOT NULL CHECK (recurrence IN ('once','daily','weekly','monthly','yearly')),
  weekdays_mask  INTEGER NOT NULL DEFAULT 0 CHECK (weekdays_mask BETWEEN 0 AND 127),
  action         TEXT NOT NULL CHECK (action IN ('reminder','launch_item','command')),
  action_ref     TEXT,                 -- item id or command id
  sound          TEXT,
  enabled        INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0,1)),
  next_fire_utc  TEXT,
  last_fired_utc TEXT,                 -- idempotency: never fire the same occurrence twice
  created_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  updated_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  CHECK (recurrence <> 'once' OR local_date IS NOT NULL),
  CHECK (recurrence <> 'weekly' OR weekdays_mask > 0),
  CHECK (action = 'reminder' OR action_ref IS NOT NULL)
);
CREATE INDEX alarms_next ON alarms(enabled, next_fire_utc);

-- Task rules: exclude a window/app from the dock, override its icon, or force a group.
CREATE TABLE task_rules (
  id          TEXT PRIMARY KEY,
  match_kind  TEXT NOT NULL CHECK (match_kind IN ('exe','aumid','window_class','title_regex')),
  match_value TEXT NOT NULL,
  rule        TEXT NOT NULL CHECK (rule IN ('exclude','icon_override','group_as')),
  value       TEXT,                    -- icon path or group key
  created_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  updated_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  CHECK (rule = 'exclude' OR value IS NOT NULL),
  UNIQUE (match_kind, match_value, rule)
);

-- Monitor layout profiles. The signature is a hash of the sorted monitor ids, resolutions and arrangement.
CREATE TABLE monitor_profiles (
  id             TEXT PRIMARY KEY,
  signature      TEXT NOT NULL UNIQUE,
  description    TEXT NOT NULL,        -- '2 monitors: 2560x1440 + 1920x1080'
  layout_json    TEXT NOT NULL CHECK (json_valid(layout_json)),  -- container id -> position fields
  last_used_utc  TEXT,
  created_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  updated_at     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

-- Sound schemes and per-event sounds.
CREATE TABLE sound_schemes (
  id          TEXT PRIMARY KEY,
  name        TEXT NOT NULL UNIQUE,
  is_builtin  INTEGER NOT NULL DEFAULT 0 CHECK (is_builtin IN (0,1)),
  created_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  updated_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE TABLE sound_events (
  scheme_id   TEXT NOT NULL REFERENCES sound_schemes(id) ON DELETE CASCADE,
  event       TEXT NOT NULL CHECK (event IN ('launch','remove','insert','attention','menu_open','menu_close','alarm','drop','minimize','restore','error')),
  sound_path  TEXT NOT NULL,
  PRIMARY KEY (scheme_id, event)
);

-- Keep updated_at honest.
CREATE TRIGGER containers_touch AFTER UPDATE ON containers WHEN NEW.updated_at = OLD.updated_at
BEGIN UPDATE containers SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id = NEW.id; END;
CREATE TRIGGER items_touch AFTER UPDATE ON items WHEN NEW.updated_at = OLD.updated_at
BEGIN UPDATE items SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id = NEW.id; END;
CREATE TRIGGER shelf_tabs_touch AFTER UPDATE ON shelf_tabs WHEN NEW.updated_at = OLD.updated_at
BEGIN UPDATE shelf_tabs SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id = NEW.id; END;
CREATE TRIGGER widget_configs_touch AFTER UPDATE ON widget_configs WHEN NEW.updated_at = OLD.updated_at
BEGIN UPDATE widget_configs SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE item_id = NEW.item_id; END;
CREATE TRIGGER alarms_touch AFTER UPDATE ON alarms WHEN NEW.updated_at = OLD.updated_at
BEGIN UPDATE alarms SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id = NEW.id; END;

-- A container must never end up nested inside itself: moving container X under item I is
-- blocked when I's container chain reaches X. The check is in the app (a recursive CTE in
-- SettingsStore.MoveContainer) because SQLite triggers can't express it cleanly.
--   WITH RECURSIVE chain(cid) AS (
--     SELECT container_id FROM items WHERE id = :target_item
--     UNION SELECT i.container_id FROM containers c JOIN items i ON i.id = c.parent_item_id JOIN chain ON c.id = chain.cid)
--   SELECT 1 FROM chain WHERE cid = :moving_container;

-- Seed: a default theme and a default bottom dock.
INSERT INTO themes (id, name, author, folder, is_builtin) VALUES
  ('00000000-0000-4000-8000-000000000001', 'Amper Glass', 'Ampersite LLC', 'amper-glass', 1);
INSERT INTO containers (id, kind, name, edge, alignment) VALUES
  ('00000000-0000-4000-8000-000000000010', 'dock', 'Main dock', 'bottom', 0.5);

PRAGMA user_version = 1;
