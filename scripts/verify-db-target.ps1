<#
    Проверка цели подключения ДО любого изменения схемы.

    Скрипт разрешает конфигурацию тем же способом, которым это делает EF
    tooling: ведущий проект Chernika.Web читает appsettings.json и
    appsettings.Development.json, поверх них — переменные окружения с
    префиксом ConnectionStrings__.

    Печатаются только сервер, порт и имя базы. Пароль и полная строка
    подключения не выводятся никогда: разбор идёт через
    DbConnectionStringBuilder, значение Password не читается.

    Использование:
        powershell -ExecutionPolicy Bypass -File scripts\verify-db-target.ps1 `
            -ExpectedDatabase chernika_migtest

    Коды возврата:
        0 — цель подтверждена, изменение схемы можно выполнять
        1 — строка подключения не разрешена
        2 — имя базы из конфигурации не совпадает с ожидаемым
        3 — подтверждение живым запросом не удалось
        4 — current_database() вернул неожиданную базу
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ExpectedDatabase,

    [string]$StartupProject = 'src\Chernika.Web\Chernika.Web.csproj'
)

$ErrorActionPreference = 'Stop'

# ── Разрешение конфигурации как у EF tooling ──
# Порядок источников повторяет WebApplicationBuilder: сначала appsettings.json,
# поверх него appsettings.Development.json, поверх них переменные окружения с
# префиксом ConnectionStrings__. Переменная окружения имеет приоритет, иначе
# подмена цели подключения осталась бы незамеченной — именно так в PR № 3
# миграция ушла в рабочую базу вместо тестовой.
$startupDir = Split-Path $StartupProject -Parent
$connectionString = $null

foreach ($file in @('appsettings.json', 'appsettings.Development.json')) {
    $path = Join-Path $startupDir $file
    if (-not (Test-Path -LiteralPath $path)) { continue }

    $config = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($config.ConnectionStrings -and $config.ConnectionStrings.DefaultConnection) {
        $connectionString = $config.ConnectionStrings.DefaultConnection
    }
}

$source = 'файлы конфигурации'
if ($env:ConnectionStrings__DefaultConnection) {
    $connectionString = $env:ConnectionStrings__DefaultConnection
    $source = 'переменная окружения ConnectionStrings__DefaultConnection'
}

if (-not $connectionString) {
    Write-Error 'Строка подключения не разрешена: ConnectionStrings__DefaultConnection.'
    exit 1
}

# ── Разбор без доступа к секретам ──
# DbConnectionStringBuilder не понимает набор ключей Npgsql и сложил бы всю
# строку под ключ ConnectionString. Поэтому разбор явный и минимальный.
# Password здесь не читается и не выводится.
$parts = @{}
foreach ($pair in $connectionString -split ';') {
    if ($pair -notmatch '=') { continue }
    $key, $value = $pair -split '=', 2
    $parts[$key.Trim().ToLowerInvariant()] = $value.Trim()
}

function Get-Part([string[]]$names, [string]$fallback) {
    foreach ($n in $names) { if ($parts.ContainsKey($n)) { return $parts[$n] } }
    return $fallback
}

$server = Get-Part @('host', 'server') ''
$database = Get-Part @('database', 'initial catalog') ''
$username = Get-Part @('username', 'user id', 'userid') 'postgres'

# Порт по умолчанию Npgsql — 5432.
$port = 5432
if ($parts.ContainsKey('port')) { $port = [int]$parts['port'] }

Write-Host 'Цель подключения, разрешённая из конфигурации EF tooling:'
Write-Host ("  Источник:  {0}" -f $source)
Write-Host ("  Сервер:    {0}" -f $server)
Write-Host ("  Порт:      {0}" -f $port)
Write-Host ("  База:      {0}" -f $database)
Write-Host ("  Ожидалась: {0}" -f $ExpectedDatabase)

if ($database -ne $ExpectedDatabase) {
    Write-Error ("База не совпадает с ожидаемой. Изменение схемы запрещено. Фактически: {0}" -f $database)
    exit 2
}

# ── Подтверждение на живом соединении: current_database() ──
$env:PGPASSWORD = $parts['password']

# psql не обязана быть в PATH: ищем в стандартных местах установки PostgreSQL.
$psqlPath = $null

$command = Get-Command psql -ErrorAction SilentlyContinue
if ($command) { $psqlPath = $command.Source }

if (-not $psqlPath) {
    # psql не обязана быть в PATH: ищем в стандартных местах установки PostgreSQL.
    $candidate = Get-ChildItem -Path 'C:\Program Files\PostgreSQL' -Filter 'psql.exe' `
        -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($candidate) { $psqlPath = $candidate.FullName }
}

if (-not $psqlPath) {
    Write-Error 'psql не найдена: подтвердить цель живым запросом невозможно.'
    exit 3
}
$actual = & $psqlPath -h $server -p $port -U $username -d $database -tAc 'SELECT current_database()' 2>$null
$actual = ($actual | Select-Object -First 1 | ForEach-Object { $_.Trim() })

if ($LASTEXITCODE -ne 0 -or -not $actual) {
    Write-Error 'Не удалось подтвердить цель живым запросом к current_database().'
    exit 3
}

if ($actual -ne $ExpectedDatabase) {
    Write-Error ("current_database() вернул {0}, ожидалась {1}. Изменение схемы запрещено." -f $actual, $ExpectedDatabase)
    exit 4
}

Write-Host ("Подтверждено запросом current_database(): {0}" -f $actual)
exit 0