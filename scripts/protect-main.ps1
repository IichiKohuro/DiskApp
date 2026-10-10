<#
.SYNOPSIS
    Включает защиту ветки main на GitHub: запрет принудительной отправки и удаления.

.DESCRIPTION
    PLAN.md, подэтап 0.5. Обязательные pull request и обязательные проверки намеренно не включаются:
    готовые ветки сливаются в main локально (CONTRIBUTING.md, раздел 4), а CI проверяет каждый коммит
    в main уже после отправки. Нужен авторизованный GitHub CLI: gh auth login.

.EXAMPLE
    gh auth login
    .\scripts\protect-main.ps1
#>
param(
    [string]$Repository = 'IichiKohuro/DiskApp',
    [string]$Branch = 'main'
)

$ErrorActionPreference = 'Stop'

gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI не авторизован: выполните gh auth login' }

$settings = @{
    required_status_checks        = $null
    enforce_admins                = $false
    required_pull_request_reviews = $null
    restrictions                  = $null
    allow_force_pushes            = $false
    allow_deletions               = $false
} | ConvertTo-Json -Depth 3

$settings | gh api --method PUT "repos/$Repository/branches/$Branch/protection" --input - | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Не удалось применить защиту ветки' }

Write-Host "Ветка $Branch в $Repository защищена: без force-push и удаления."
