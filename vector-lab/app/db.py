"""Where the Postgres comes from.

1. VECTOR_LAB_DB_URL (environment, or a .env file in vector-lab/): your Supabase or any Postgres with the pgvector extension.
   Supabase: Dashboard > Connect > connection string. Use the direct connection or the Session pooler (port 5432), not the Transaction pooler.
2. Otherwise a local Postgres 16 with pgvector started by the `pgserver` package (no Docker, data in ./data/pg). Development and tests only.
"""
from __future__ import annotations

import os
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
_server = None


def database_url(allow_local: bool = True) -> str:
    try:
        from dotenv import load_dotenv
        load_dotenv(ROOT / ".env")
    except ImportError:
        pass
    url = os.environ.get("VECTOR_LAB_DB_URL")
    if url:
        return url
    if not allow_local:
        raise RuntimeError("Set VECTOR_LAB_DB_URL to a Postgres connection string (see .env.example).")
    global _server
    if _server is None:
        import pgserver
        (ROOT / "data").mkdir(exist_ok=True)
        _server = pgserver.get_server(ROOT / "data" / "pg")
    return _server.get_uri()


def describe(url: str) -> str:
    """The URL without its password, for logs."""
    return url.split("@")[-1] if "@" in url else url
