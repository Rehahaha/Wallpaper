# 把本地仓库推到 GitHub
# 用法：
#   1) 先在 https://github.com/new 建一个空仓库（不要勾选 README/.gitignore/License）
#   2) powershell -ExecutionPolicy Bypass -File tools/publish-github.ps1 -RepoUrl https://github.com/<你的账号>/<仓库名>.git
#
# 说明：git push 走的是 https 协议，只需要账号 + PAT（Personal Access Token），
#       不需要 GitHub API，因此在 api.github.com 被限制的网络下也能用。

param(
    [Parameter(Mandatory = $true)]
    [string]$RepoUrl,
    [string]$Branch = "main"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Test-Path (Join-Path $root ".git"))) {
    throw "当前目录不是 git 仓库"
}

$existing = & git remote 2>$null
if ($existing -contains "origin") {
    Write-Host "已有 origin，更新为 $RepoUrl"
    & git remote set-url origin $RepoUrl
} else {
    & git remote add origin $RepoUrl
}

$currentBranch = (& git rev-parse --abbrev-ref HEAD).Trim()
if ($currentBranch -ne $Branch) {
    Write-Host "当前分支 $currentBranch，重命名为 $Branch"
    & git branch -M $Branch
}

Write-Host "推送中（首次会要求登录 GitHub，密码处请填 Personal Access Token）..." -ForegroundColor Cyan
& git push -u origin $Branch

Write-Host ""
Write-Host "完成。仓库地址：$($RepoUrl -replace '\.git$','')" -ForegroundColor Green
