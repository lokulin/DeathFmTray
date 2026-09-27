namespace DeathFmTray;

/// <summary>
/// This app's own Last.fm/Discord application identifiers - not per-user
/// secrets. They just identify "Death.FM Player" to those services, the same
/// way any published app embeds its own API key; they grant no access to
/// anyone's account by themselves. Each user still authorizes their own
/// Last.fm account separately via the existing Connect flow (see
/// LastFmScrobbler's auth.getToken/auth.getSession flow), which is what
/// actually gates access to that user's scrobbles.
/// </summary>
internal static class AppCredentials
{
    public const string LastFmApiKey = "53a48adcaf85d2da5e316b0cd4b2d53c";
    public const string LastFmApiSecret = "07f6d35faa283f3da90ba7ce79c81528";
    public const string DiscordClientId = "1550074280274956320";
    public const string DiscordDefaultImageKey = "fa6a3d3b-3721-4a49-9542-d91ddbf13252";
}
