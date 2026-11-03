namespace BillingOss.App.Persistence;

/// <summary>
/// Lo que EF no expresa en la migración de contenidos: claves foráneas con borrado en cascada, checks, la columna de
/// búsqueda generada con su índice y los índices por expresión. Las invariantes van primero en la base (D-02).
/// </summary>
internal static class ContentSql
{
    /// <summary>Pliega las tildes de una expresión SQL, igual que la columna de búsqueda (sección 3.2 de diseno/contenidos.md).</summary>
    public static string Fold(string expression) => $"translate({expression}, 'áéíóúüÁÉÍÓÚÜ', 'aeiouuAEIOUU')";

    public const string Up = """
        -- Categorías: la madre es del mismo tipo y no es la propia categoría.
        ALTER TABLE content.categories ADD CONSTRAINT uq_categories_id_kind UNIQUE (id, kind);
        ALTER TABLE content.categories ADD CONSTRAINT fk_categories_parent FOREIGN KEY (parent_id, kind) REFERENCES content.categories (id, kind);
        ALTER TABLE content.categories ADD CONSTRAINT ck_categories_parent CHECK (parent_id IS NULL OR parent_id <> id);
        ALTER TABLE content.categories ADD CONSTRAINT ck_categories_texts CHECK (jsonb_typeof(texts) = 'object');

        -- Artículos y sus hijos: los textos y los votos se borran con el artículo.
        ALTER TABLE content.articles ADD CONSTRAINT fk_articles_category FOREIGN KEY (category_id) REFERENCES content.categories (id);
        ALTER TABLE content.articles ADD CONSTRAINT ck_articles_audience CHECK (audience <> 'Targeted' OR cardinality(product_ids) + cardinality(group_ids) > 0);
        ALTER TABLE content.articles ADD CONSTRAINT ck_articles_counters CHECK (views >= 0 AND helpful >= 0 AND unhelpful >= 0);
        ALTER TABLE content.article_texts ADD CONSTRAINT fk_article_texts_article FOREIGN KEY (article_id) REFERENCES content.articles (id) ON DELETE CASCADE;
        ALTER TABLE content.article_votes ADD CONSTRAINT fk_article_votes_article FOREIGN KEY (article_id) REFERENCES content.articles (id) ON DELETE CASCADE;

        -- Búsqueda de texto completo: título con peso A, resumen y etiquetas con B, cuerpo con C. La configuración sale del
        -- idioma de la fila y las tildes se pliegan, así "configuracion" encuentra "configuración".
        ALTER TABLE content.article_texts ADD COLUMN search tsvector GENERATED ALWAYS AS (
          setweight(to_tsvector(CASE language WHEN 'en' THEN 'english'::regconfig ELSE 'spanish'::regconfig END,
            translate(title, 'áéíóúüÁÉÍÓÚÜ', 'aeiouuAEIOUU')), 'A')
          || setweight(to_tsvector(CASE language WHEN 'en' THEN 'english'::regconfig ELSE 'spanish'::regconfig END,
            translate(coalesce(summary, '') || ' ' || tags, 'áéíóúüÁÉÍÓÚÜ', 'aeiouuAEIOUU')), 'B')
          || setweight(to_tsvector(CASE language WHEN 'en' THEN 'english'::regconfig ELSE 'spanish'::regconfig END,
            translate(body, 'áéíóúüÁÉÍÓÚÜ', 'aeiouuAEIOUU')), 'C')) STORED;
        CREATE INDEX ix_article_texts_search ON content.article_texts USING gin (search);

        -- Novedades.
        ALTER TABLE content.announcements ADD CONSTRAINT ck_announcements_audience CHECK (audience <> 'Targeted' OR cardinality(product_ids) + cardinality(group_ids) > 0);
        ALTER TABLE content.announcements ADD CONSTRAINT ck_announcements_dates CHECK (expires_at IS NULL OR publish_at IS NULL OR expires_at > publish_at);
        ALTER TABLE content.announcements ADD CONSTRAINT ck_announcements_texts CHECK (jsonb_typeof(texts) = 'object');

        -- Descargas: no se publica sin archivo, y los bytes se borran con la descarga.
        ALTER TABLE content.downloads ADD CONSTRAINT fk_downloads_category FOREIGN KEY (category_id) REFERENCES content.categories (id);
        ALTER TABLE content.downloads ADD CONSTRAINT ck_downloads_audience CHECK (audience <> 'Targeted' OR cardinality(product_ids) + cardinality(group_ids) > 0);
        ALTER TABLE content.downloads ADD CONSTRAINT ck_downloads_published CHECK (status <> 'Published' OR size IS NOT NULL);
        ALTER TABLE content.downloads ADD CONSTRAINT ck_downloads_numbers CHECK (download_count >= 0 AND (size IS NULL OR size >= 0));
        ALTER TABLE content.downloads ADD CONSTRAINT ck_downloads_texts CHECK (jsonb_typeof(texts) = 'object');
        ALTER TABLE content.download_files ADD CONSTRAINT fk_download_files_download FOREIGN KEY (download_id) REFERENCES content.downloads (id) ON DELETE CASCADE;

        -- Estado de la red: un componente con historial no se borra, y las novedades y los vínculos siguen a su evento.
        ALTER TABLE content.components ADD CONSTRAINT fk_components_parent FOREIGN KEY (parent_id) REFERENCES content.components (id);
        ALTER TABLE content.components ADD CONSTRAINT ck_components_parent CHECK (parent_id IS NULL OR parent_id <> id);
        ALTER TABLE content.status_event_components ADD CONSTRAINT fk_status_event_components_event FOREIGN KEY (event_id) REFERENCES content.status_events (id) ON DELETE CASCADE;
        ALTER TABLE content.status_event_components ADD CONSTRAINT fk_status_event_components_component FOREIGN KEY (component_id) REFERENCES content.components (id);
        ALTER TABLE content.status_event_updates ADD CONSTRAINT fk_status_event_updates_event FOREIGN KEY (event_id) REFERENCES content.status_events (id) ON DELETE CASCADE;
        ALTER TABLE content.status_events ADD CONSTRAINT ck_status_events_window CHECK (ends_at IS NULL OR ends_at >= starts_at);
        ALTER TABLE content.status_events ADD CONSTRAINT ck_status_events_maintenance CHECK (kind <> 'Maintenance' OR ends_at IS NOT NULL);

        -- Suscriptores: una casilla por suscriptor, sin distinguir mayúsculas.
        CREATE UNIQUE INDEX ix_subscribers_email ON content.subscribers (lower(email));

        -- Grupos de clientes: la cuenta apunta a su grupo (el mínimo de cuentas avanzadas).
        ALTER TABLE accounts.accounts ADD CONSTRAINT fk_accounts_group FOREIGN KEY (group_id) REFERENCES accounts.groups (id);
        """;

    /// <summary>
    /// Va antes de que EF borre las tablas, que no conoce las claves foráneas de arriba: suelta las que apuntan a una tabla
    /// que EF borra antes que la que tiene la clave (categorías antes que descargas, componentes antes que sus eventos).
    /// </summary>
    public const string Down = """
        ALTER TABLE content.downloads DROP CONSTRAINT IF EXISTS fk_downloads_category;
        ALTER TABLE content.status_event_components DROP CONSTRAINT IF EXISTS fk_status_event_components_component;
        ALTER TABLE accounts.accounts DROP CONSTRAINT IF EXISTS fk_accounts_group;
        """;
}
