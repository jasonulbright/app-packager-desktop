using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.Wsus;

/// <summary>The WSUS Updates window keeps its commands locked while a write to the server works.</summary>
public sealed class WsusUpdatesBusyTests
{
    /// <summary>The fake server, with SetStateAsync held until the test opens the gate.</summary>
    private sealed class GatedClient(FakeWsusAdminClient inner) : IWsusAdminClient
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ListGate { get; set; }
        public Task<WsusServerStatus> GetStatusAsync(WsusSettings s, Action<string>? p = null, CancellationToken ct = default) => inner.GetStatusAsync(s, p, ct);
        public Task<WsusServerStatus> CreateSelfSignedCertificateAsync(WsusSettings s, Action<string>? p = null, CancellationToken ct = default) => inner.CreateSelfSignedCertificateAsync(s, p, ct);
        public Task<WsusServerStatus> ImportPfxAsync(WsusSettings s, string pfx, string pw, Action<string>? p = null, CancellationToken ct = default) => inner.ImportPfxAsync(s, pfx, pw, p, ct);
        public Task<string> ExportCertificateAsync(WsusSettings s, string cer, Action<string>? p = null, CancellationToken ct = default) => inner.ExportCertificateAsync(s, cer, p, ct);
        public Task<IReadOnlyList<string>> GetComputerGroupNamesAsync(WsusSettings s, Action<string>? p = null, CancellationToken ct = default) => inner.GetComputerGroupNamesAsync(s, p, ct);
        public async Task<IReadOnlyList<WsusPublishedUpdate>> ListAsync(WsusSettings s, bool other = false, Action<string>? p = null, CancellationToken ct = default)
        {
            var result = await inner.ListAsync(s, other, p, ct);
            if (ListGate is { } gate) await gate.Task;
            return result;
        }
        public async Task<IReadOnlyList<WsusItemResult>> SetStateAsync(WsusSettings s, IEnumerable<Guid> ids, string action, string? group = null, Action<string>? p = null, CancellationToken ct = default)
        {
            await Gate.Task;
            return await inner.SetStateAsync(s, ids, action, group, p, ct);
        }
        public Task<IReadOnlyList<WsusItemResult>> ImportCatalogUpdatesAsync(WsusSettings s, IEnumerable<Guid> ids, Action<string>? p = null, CancellationToken ct = default) => inner.ImportCatalogUpdatesAsync(s, ids, p, ct);
    }

    private static WsusPublishedUpdate Update() => new(Guid.NewGuid().ToString(), "Tool 1.0", "Application", "tag", "1.0", null, "", "", false, false, false);

    [Fact]
    public async Task TheShowOtherPublishersBoxDuringAWriteDoesNotUnlockTheCommandsAndReadsOnceAfterTheWrite()
    {
        var fake = new FakeWsusAdminClient { Updates = [Update()] };
        var client = new GatedClient(fake);
        var vm = new WsusUpdatesViewModel(client, new WsusSettings { ServerName = "wsus01" }, new FakeWsusPrompts());
        await vm.LoadAsync();
        vm.SetSelection(vm.Updates);

        var write = vm.DeclineCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => vm.Busy, TimeSpan.FromSeconds(10)));

        vm.ShowOtherPublishers = true;
        // Give a wrongly started read time to run and finish.
        SpinWait.SpinUntil(() => fake.Calls.Contains("List:True"), TimeSpan.FromMilliseconds(500));

        Assert.True(vm.Busy);
        Assert.False(vm.DeclineCommand.CanExecute(null));
        Assert.False(vm.RemoveCommand.CanExecute(null));
        Assert.DoesNotContain("List:True", fake.Calls);

        client.Gate.SetResult();
        await write;

        Assert.False(vm.Busy);
        Assert.Single(fake.Calls, c => c == "List:True");
    }

    [Fact]
    public async Task TwoToggledBoxesInARowReadOnceMore()
    {
        var fake = new FakeWsusAdminClient { Updates = [Update()] };
        var client = new GatedClient(fake) { ListGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new WsusUpdatesViewModel(client, new WsusSettings { ServerName = "wsus01" }, new FakeWsusPrompts());

        // The first read is held on the server while the box is toggled twice.
        var first = vm.LoadAsync();
        Assert.True(SpinWait.SpinUntil(() => fake.Calls.Contains("List:False"), TimeSpan.FromSeconds(10)));
        vm.ShowOtherPublishers = true;
        vm.ShowOtherPublishers = false;
        Assert.Single(fake.Calls, c => c.StartsWith("List:", StringComparison.Ordinal));

        client.ListGate.SetResult();
        await first;

        Assert.False(vm.Busy);
        var reads = fake.Calls.Where(c => c.StartsWith("List:", StringComparison.Ordinal)).ToList();
        Assert.Equal(["List:False", "List:False"], reads);
    }
}
