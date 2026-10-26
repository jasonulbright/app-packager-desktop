param()
Get-CMSite | ForEach-Object {
    [pscustomobject]@{
        SiteCode   = [string]$_.SiteCode
        SiteName   = [string]$_.SiteName
        ServerName = [string]$_.ServerName
        Version    = [string]$_.Version
    }
}
