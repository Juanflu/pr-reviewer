# Métricas de Pull Requests

Web estática (sin dependencias ni servidor) para explorar el CSV de métricas de PRs de Azure DevOps.

## Uso

1. Genera los datos a partir del CSV (solo necesita `python3`):

   ```sh
   python3 build_data.py                      # usa ~/Downloads/PullRequests_Metrics.csv
   python3 build_data.py ruta/a/otro.csv      # o un CSV concreto
   ```

   Esto crea/actualiza `data.js`.

2. Abre `index.html` en el navegador (doble clic o `open index.html`).

## Qué incluye

- Filtros: rango de fechas (con atajos), autor, estado, proyecto, repositorio (limitado al proyecto), rama destino y búsqueda en título/ID. Todos admiten "Todos".
- KPIs: nº de PRs, media, mediana y P90 del tiempo en revisión, % de abandonadas.
- Gráfico de evolución por semana o mes (nº de PRs + mediana/media de horas).
- Tabla agrupada por autor, proyecto, repositorio, rama, estado o mes. Clic en una fila para filtrar.
- Detalle de PRs ordenable y paginado, con enlace a Azure DevOps.
- Los filtros se guardan en la URL (`#...`), así que puedes guardar o compartir una vista.

Notas: el tiempo en revisión es `HoursOpen` (horas naturales, incluye noches y fines de semana). Las PRs activas cuentan en el nº de PRs pero no en las métricas de tiempo.
