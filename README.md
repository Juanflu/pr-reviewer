# Métricas de Pull Requests

Web estática (sin dependencias ni servidor) para explorar el CSV de métricas de PRs de Azure DevOps.

## Exportar datos desde Azure DevOps

`export/ExportPullRequests.cs` genera `PullRequests_Metrics.csv` con las PRs y, para cada una, nº de commits,
archivos cambiados y líneas añadidas/borradas (sin contar archivos generados). Requiere el SDK de **.NET 10 o superior**
(se ejecuta como app de un solo archivo, sin proyecto). Comprueba la versión con `dotnet --version`.

1. Crea un PAT en Azure DevOps con alcance **Code > Read**.
2. Copia `export/config.example.json` a `export/config.json` (ignorado por git) y rellena organización,
   proyectos, autores y, si quieres, los patrones de `excludePatterns`.
3. Desde la raíz del repo:

   ```sh
   export AZDO_PAT=xxxxxxxx
   dotnet run export/ExportPullRequests.cs
   ```

El `nuget.config` de la raíz limita la restauración a nuget.org, para que no intente usar feeds privados
configurados en tu equipo (si no, puede fallar con `NU1301 … 401 (Unauthorized)`).

Los detalles de cada PR se guardan en `export/.cache/` (ignorado por git): las siguientes ejecuciones solo
consultan las PRs nuevas o activas. Si cambias `excludePatterns`, se recalcula todo.

Las líneas se obtienen con la API `FileDiffs` (la que usa la web de Azure DevOps, no documentada); si no
está disponible, el script descarga cada archivo antes y después del cambio y compara sus líneas.

## Uso

1. Genera los datos a partir del CSV (solo necesita `python3`):

   ```sh
   python3 build_data.py                      # usa ./PullRequests_Metrics.csv o ~/Downloads/PullRequests_Metrics.csv
   python3 build_data.py ruta/a/otro.csv      # o un CSV concreto
   ```

   Esto crea/actualiza `data.js`. Contiene los datos reales, así que está en `.gitignore` y nunca se sube.

2. Abre `index.html` en el navegador (doble clic o `open index.html`).

## Versión publicada (GitHub Pages)

https://juanflu.github.io/pr-reviewer/

El workflow `.github/workflows/pages.yml` publica **solo `index.html`**, sin `data.js`, para que los datos
no queden públicos. Al abrir la web publicada se pide el CSV (seleccionar o arrastrar); se procesa solo en el
navegador y se guarda en ese navegador para no tener que cargarlo cada vez ("Quitar datos" lo borra).

> En *Settings → Pages* la fuente debe ser **GitHub Actions**.

## Qué incluye

- Filtros: rango de fechas (con atajos), autor, estado, proyecto, repositorio (limitado a los proyectos elegidos), rama destino y búsqueda en título/ID. Autor, estado, proyecto, repositorio y rama son de selección múltiple ("Todos", "Ninguno", "solo" en cada opción; p. ej. todos los autores menos dos).
- KPIs: nº de PRs, media, mediana y P90 del tiempo en revisión, % de abandonadas.
- Gráfico de evolución por semana o mes (nº de PRs + mediana/media de horas).
- Tabla agrupada por autor, proyecto, repositorio, rama, estado o mes. Clic en una fila para filtrar.
- Detalle de PRs ordenable y paginado, con enlace a Azure DevOps.
- Pestaña **Comparar periodos**: A frente a B (mismo tramo del año, años completos, antes/después de una fecha
  o fechas libres) con KPIs y variación, PRs por autor activo y mes, evolución mes a mes superpuesta,
  distribución del tiempo en revisión por tramos y tabla por autor. Si el CSV trae tamaño de PR: medianas de
  líneas/archivos/commits, horas de revisión por 100 líneas y tiempo de revisión por tramo de tamaño (XS–XL).
- Los filtros se guardan en la URL (`#...`), así que puedes guardar o compartir una vista.

Notas: el tiempo en revisión es `HoursOpen` (horas naturales, incluye noches y fines de semana). Las PRs activas cuentan en el nº de PRs pero no en las métricas de tiempo.
