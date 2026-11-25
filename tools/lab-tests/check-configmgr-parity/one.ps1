param([string]$Name)
$ErrorActionPreference='Continue'
Import-Module "$($env:SMS_ADMIN_UI_PATH)\..\ConfigurationManager.psd1" -ErrorAction Stop
if(-not (Get-PSDrive -Name MCM -ErrorAction SilentlyContinue)){ New-PSDrive -Name MCM -PSProvider CMSite -Root 'cm01.contoso.com' | Out-Null }
Set-Location 'C:\guide\ps-run\Packagers'
$common=@{ SiteCode='MCM'; FileServerPath='\\cm01.contoso.com\ContentShare$'; DownloadRoot='C:\guide\ap-dl' }
$a = & ".\package-$Name.ps1" @common -StageOnly 2>&1 | Out-String -Width 250
$b = & ".\package-$Name.ps1" @common -PackageOnly 2>&1 | Out-String -Width 250
($a + "`n====PACKAGE====`n" + $b)
