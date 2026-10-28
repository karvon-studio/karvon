# Notes

A notes API (`api`, port 8000) and the store that keeps its notes (`store`), run with Docker Compose:
`docker compose up -d --build`, then `GET /health`, `GET /notes` and `POST /notes` with `{"text": "..."}`.
