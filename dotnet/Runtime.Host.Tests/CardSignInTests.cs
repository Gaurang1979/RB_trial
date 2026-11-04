using Grpc.Core;
using VisionStudio.Remote.V1;
using VisionSystem.Runtime;
using Xunit;

/// <summary>Badge (card) and password sign-in in the real runtime, and the idle time it tells
/// Vision Studio. Card numbers are kept only as hashes and never accepted over the network.</summary>
public sealed class CardSignInTests : IAsyncLifetime
{
    const string Card = "0004211337";
    RuntimeFixture _f = null!;
    string Data => Path.Combine(_f.Folder, "data");

    public async Task InitializeAsync() => _f = await RuntimeFixture.StartAsync(f =>
    {
        var store = new AccessStore(Path.Combine(f.Folder, "data"));
        store.SaveUser("sup", "Shift Supervisor", [Role.Supervisor], null, "2580", Card);
        store.SaveUser("qa", "QA Inspector", [Role.Qa], null, null, "0007777777");
    }, "--Runtime:StudioIdleSignOut=00:03:00");
    public async Task DisposeAsync() => await _f.DisposeAsync();

    static SignInRequest ByCard(string card) => new() { Card = card, Client = ClientKind.Studio };

    [Fact]
    public async Task A_known_card_signs_in_locally_and_Studio_is_told_the_idle_time()
    {
        var r = await _f.Sessions().SignInAsync(ByCard(" " + Card + " "));
        Assert.True(r.Ok, r.ErrorText);
        Assert.Equal("sup", r.User.Name);
        var who = await _f.Sessions().WhoAmIAsync(new WhoAmIRequest(), RuntimeFixture.Bearer(r.Token));
        Assert.True(who.MayOperate);
        Assert.Equal(180u, who.IdleSignOutSeconds);
        var users = await _f.Sessions().ListUsersAsync(new ListUsersRequest());
        Assert.Contains(users.Users, u => u.Name == "sup" && u.HasCard && u.HasPin);
        Assert.Contains(users.Users, u => u.Name == "op" && !u.HasCard);
    }

    [Fact]
    public async Task A_card_over_the_network_is_refused_even_a_known_one()
    {
        var r = await _f.Sessions(network: true).SignInAsync(ByCard(Card));
        Assert.False(r.Ok);
        Assert.Equal("runtime.signIn.cardLocalOnly", r.ErrorKey);
        Assert.Equal("", r.Token);
    }

    [Fact]
    public async Task Unknown_cards_lock_card_sign_in_but_not_the_PIN()
    {
        for (var i = 0; i < 5; i++)
            Assert.Equal("runtime.signIn.cardUnknown", (await _f.Sessions().SignInAsync(ByCard($"99999999{i:00}"))).ErrorKey);
        var locked = await _f.Sessions().SignInAsync(ByCard(Card));
        Assert.Equal(("runtime.signIn.locked", 429u), (locked.ErrorKey, locked.ErrorCode));
        await _f.SignInOperator();   // the PIN pad still works
    }

    [Fact]
    public async Task A_good_card_does_not_clear_the_count_of_unknown_ones()
    {
        for (var i = 0; i < 4; i++) await _f.Sessions().SignInAsync(ByCard($"88888888{i:00}"));
        Assert.True((await _f.Sessions().SignInAsync(ByCard(Card))).Ok);
        await _f.Sessions().SignInAsync(ByCard("8888888899"));
        Assert.Equal("runtime.signIn.locked", (await _f.Sessions().SignInAsync(ByCard(Card))).ErrorKey);
    }

    [Fact]
    public void A_broken_or_cut_short_hash_matches_nothing()
    {
        var store = new AccessStore(Data);
        var path = Path.Combine(Data, "runtime-users.json");
        var good = store.Find("sup")!.CardHash!;
        var salt = good.Split('$')[1];
        // Edit the user's hash in the JSON, not the file's text: the file escapes '+' (\u002B), and a
        // cut-short hash is the start of every other hash with the same salt. Each write gets a later
        // file time than the last, so the store sees every change.
        var at = File.GetLastWriteTimeUtc(path);
        void SetCardHash(string hash)
        {
            var users = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsArray();
            users.Single(u => (string?)u!["Name"] == "sup")!["CardHash"] = hash;
            File.WriteAllText(path, users.ToJsonString());
            File.SetLastWriteTimeUtc(path, at = at.AddSeconds(1));
        }
        foreach (var broken in new[] { $"100000${salt}$", $"100000${salt}$AQ==", $"1${salt}${good.Split('$')[2]}", "nonsense" })
        {
            SetCardHash(broken);
            Assert.Null(store.FindByCard(Card));
            Assert.False(AccessStore.Verify(broken, "2580"));
            SetCardHash(good);
            Assert.Equal("sup", store.FindByCard(Card)?.Name);
        }
        Assert.Equal("qa", store.FindByCard("0007777777")?.Name);
    }

    [Fact]
    public async Task Password_sign_in_and_a_plant_token_get_no_idle_time()
    {
        var r = await _f.Sessions().SignInAsync(new SignInRequest { User = "eng", Password = "secret-pass", Client = ClientKind.Studio });
        Assert.True(r.Ok);
        Assert.Equal(180u, (await _f.Sessions().WhoAmIAsync(new WhoAmIRequest(), RuntimeFixture.Bearer(r.Token))).IdleSignOutSeconds);
        Assert.Equal("runtime.signIn.wrong", (await _f.Sessions().SignInAsync(new SignInRequest { User = "eng", Password = "nope" })).ErrorKey);
        var token = _f.AddToken("mes", operate: true);
        Assert.Equal(0u, (await _f.Sessions(network: true).WhoAmIAsync(new WhoAmIRequest(), RuntimeFixture.Bearer(token))).IdleSignOutSeconds);
    }

    [Fact]
    public void Cards_are_stored_hashed_with_one_salt_and_never_twice()
    {
        var text = File.ReadAllText(Path.Combine(Data, "runtime-users.json"));
        Assert.DoesNotContain(Card, text);
        Assert.DoesNotContain("0007777777", text);
        var store = new AccessStore(Data);
        var salts = store.Users().Where(u => u.CardHash is not null).Select(u => u.CardHash!.Split('$')[1]).Distinct();
        Assert.Single(salts);
        Assert.Equal("qa", store.FindByCard("0007777777")?.Name);
        Assert.Null(store.FindByCard("0007777778"));
        Assert.Throws<InvalidOperationException>(() => store.SaveUser("op", "Line Operator", [Role.Operator], null, null, Card));
        Assert.Throws<InvalidOperationException>(() => store.SaveUser("op", "Line Operator", [Role.Operator], null, null, "12345"));
        store.SaveUser("hex", "Hex Card", [Role.Operator], null, null, "04a1b2c3d4");
        Assert.Equal("hex", store.FindByCard("04A1B2C3D4")?.Name);   // Caps Lock on the reader
        // saving again without --card keeps the card; an empty card removes it
        store.SaveUser("sup", "Shift Supervisor", [Role.Supervisor], null, null);
        Assert.Equal("sup", store.FindByCard(Card)?.Name);
        store.SaveUser("sup", "Shift Supervisor", [Role.Supervisor], null, null, "");
        Assert.Null(store.FindByCard(Card));
    }
}
