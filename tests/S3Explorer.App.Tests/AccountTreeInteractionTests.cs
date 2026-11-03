using System.Reflection;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed partial class MainFormInteractionTests
{
    [Fact]
    public async Task AccountsAndBlankSpaceExposeCreationAndToolbarDisablesInvalidMoves()
    {
        await RunMessagePumpAsync(async host =>
        {
            await using var fixture = await AccountTreeFixture.CreateAsync();
            var form = fixture.Form;
            var tree = fixture.Tree;
            host.Controls.Add(tree);
            tree.Size = new Size(350, 240);
            _ = tree.Handle;
            InvokeInstance(form, "PopulateProfiles");
            tree.SelectedNode = tree.Nodes[0];
            Assert.Same(GetField<ContextMenuStrip>(form, "_accountsMenu"), tree.ContextMenuStrip);
            Assert.Contains(tree.ContextMenuStrip!.Items.Cast<ToolStripItem>(), item => item.Text == "新建连接分组...");
            Assert.False(GetField<ToolStripButton>(form, "_accountMoveUp").Enabled);
            Assert.False(GetField<ToolStripButton>(form, "_accountMoveDown").Enabled);
            tree.SelectedNode = fixture.Account(fixture.First.Id);
            Assert.True(GetField<ToolStripButton>(form, "_accountMoveDown").Enabled);
            var blank = new Point(10, tree.ClientSize.Height - 4);
            Assert.Null(tree.GetNodeAt(blank));
            InvokeProtected(tree, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, blank.X, blank.Y, 0));
            Assert.Same(tree.Nodes[0], tree.SelectedNode);
            Assert.Same(GetField<ContextMenuStrip>(form, "_accountsMenu"), tree.ContextMenuStrip);
            var toolbar = Assert.IsType<ToolStrip>(Assert.Single(form.Controls.Find("AccountToolbar", true)));
            Assert.Contains(toolbar.Items.Cast<ToolStripItem>(), item => item.Name == "AccountNewGroup" && item.Text == "新建分组");
        });
    }

    [Fact]
    public async Task MovingAccountRetainsBucketSelectionExpansionAndObjectListing()
    {
        await RunMessagePumpAsync(async _ =>
        {
            await using var fixture = await AccountTreeFixture.CreateAsync();
            var form = fixture.Form;
            var account = fixture.Account(fixture.First.Id);
            account.Nodes.Clear();
            var bucket = account.Nodes.Add("bucket");
            bucket.Tag = new BucketNodeTag(fixture.First, "bucket");
            account.Expand();
            InvokeAccountMethod(form, "SelectTreeNodeWithoutNavigation", bucket);
            SetField(form, "_currentProfile", fixture.First);
            SetField(form, "_currentBucket", "bucket");
            SetField(form, "_currentPrefix", "folder/");
            var objects = GetField<ListView>(form, "_objects");
            var row = objects.Items.Add("keep-object-row");
            var groupNode = fixture.GroupNode;
            await InvokeAccountTask(form, "ApplyAccountTreeDropAsync", account, groupNode, false);
            Assert.Same(account, fixture.Account(fixture.First.Id));
            Assert.Same(groupNode, account.Parent);
            Assert.Same(bucket, fixture.Tree.SelectedNode);
            Assert.Same(bucket, Assert.Single(account.Nodes.Cast<TreeNode>()));
            Assert.True(account.IsExpanded);
            Assert.Same(row, Assert.Single(objects.Items.Cast<ListViewItem>()));
            Assert.Equal("folder/", GetField<string>(form, "_currentPrefix"));
            Assert.Equal(fixture.Group.Id, GetField<ConnectionProfile>(form, "_currentProfile").GroupId);
            Assert.Equal(fixture.Group.Id, Assert.IsType<BucketNodeTag>(bucket.Tag).Profile.GroupId);
            Assert.Equal(fixture.Group.Id, fixture.Store.Saved.Profiles.Single(item => item.Id == fixture.First.Id).GroupId);
            await InvokeAccountTask(form, "ApplyAccountTreeDropAsync", account, fixture.Tree.Nodes[0], false);
            Assert.Null(fixture.Store.Saved.Profiles.Single(item => item.Id == fixture.First.Id).GroupId);
            Assert.Same(bucket, fixture.Tree.SelectedNode);
        });
    }

    [Fact]
    public async Task DropEventsSortAccountsAndGroupsAndRejectBucketsForeignNodesAndFiles()
    {
        await RunMessagePumpAsync(async host =>
        {
            await using var fixture = await AccountTreeFixture.CreateAsync();
            var form = fixture.Form;
            var tree = fixture.Tree;
            host.Controls.Add(tree);
            _ = tree.Handle;
            tree.ExpandAll();
            var first = fixture.Account(fixture.First.Id);
            var second = fixture.Account(fixture.Second.Id);
            SetField(form, "_draggedAccountNode", first);
            var args = DropArgs(tree, first, second, after: true);
            InvokeProtected(tree, "OnDragOver", args);
            Assert.Equal(DragDropEffects.Move, args.Effect);
            Assert.Contains("之后", GetStatus(form));
            InvokeProtected(tree, "OnDragDrop", args);
            await WaitUntilAsync(() => fixture.Store.Saved.Profiles.Last().Id == fixture.First.Id, TimeSpan.FromSeconds(2));
            Assert.Equal([fixture.Second.Id, fixture.First.Id], tree.Nodes[0].Nodes.Cast<TreeNode>()
                .Where(node => node.Tag is ConnectionProfile).Select(node => ((ConnectionProfile)node.Tag!).Id));
            Assert.Equal(Color.Empty, second.BackColor);
            InvokeAccountMethod(form, "SelectTreeNodeWithoutNavigation", first);
            Assert.False(GetField<ToolStripButton>(form, "_accountMoveDown").Enabled);
            GetField<ToolStripButton>(form, "_accountMoveUp").PerformClick();
            await WaitUntilAsync(() => fixture.Store.Saved.Profiles.First().Id == fixture.First.Id, TimeSpan.FromSeconds(2));
            Assert.False(GetField<ToolStripButton>(form, "_accountMoveUp").Enabled);
            var bucket = second.Nodes.Add("bucket");
            bucket.Tag = new BucketNodeTag(fixture.Second, "bucket");
            second.Expand();
            var invalid = DropArgs(tree, first, bucket, after: false);
            InvokeProtected(tree, "OnDragOver", invalid);
            Assert.Equal(DragDropEffects.None, invalid.Effect);
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { "test-file.txt" });
            invalid = new DragEventArgs(data, 0, args.X, args.Y, DragDropEffects.Copy, DragDropEffects.None);
            InvokeProtected(tree, "OnDragEnter", invalid);
            Assert.Equal(DragDropEffects.None, invalid.Effect);
            using var otherTree = new TreeView();
            var foreign = otherTree.Nodes.Add("foreign");
            foreign.Tag = fixture.First;
            SetField(form, "_draggedAccountNode", foreign);
            invalid = DropArgs(tree, foreign, second, false);
            InvokeProtected(tree, "OnDragOver", invalid);
            Assert.Equal(DragDropEffects.None, invalid.Effect);

            var anotherGroup = new ConnectionGroup { Name = "Another", SortOrder = 1 };
            await InvokeAccountTask(form, "SaveConfigurationAndRefreshAsync",
                fixture.Store.Saved with { Groups = [fixture.Group, anotherGroup] });
            var target = tree.Nodes[0].Nodes.Cast<TreeNode>().Single(node => node.Tag is ConnectionGroup group && group.Id == anotherGroup.Id);
            await InvokeAccountTask(form, "ApplyAccountTreeDropAsync", fixture.GroupNode, target, true);
            Assert.Equal([anotherGroup.Id, fixture.Group.Id], fixture.Store.Saved.Groups.Select(group => group.Id));
            Assert.All(fixture.Store.Saved.Profiles, profile => Assert.Null(profile.GroupId));
        });
    }

    [Fact]
    public async Task InFlightBucketLookupUsesMovedNodeAndKeepsLatestGroup()
    {
        await RunMessagePumpAsync(async _ =>
        {
            await using var fixture = await AccountTreeFixture.CreateAsync();
            var release = new TaskCompletionSource<IReadOnlyList<BucketInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Storage.Buckets = () => release.Task;
            var account = fixture.Account(fixture.First.Id);
            var lookup = InvokeAccountTask(fixture.Form, "LoadBucketsAsync", fixture.First, account);
            await InvokeAccountTask(fixture.Form, "ApplyAccountTreeDropAsync", account, fixture.GroupNode, false);
            release.SetResult([new BucketInfo("test-bucket", null)]);
            await lookup;
            Assert.Same(account, fixture.Account(fixture.First.Id));
            var bucket = Assert.IsType<BucketNodeTag>(Assert.Single(account.Nodes.Cast<TreeNode>()).Tag);
            Assert.Equal("test-bucket", bucket.Bucket);
            Assert.Equal(fixture.Group.Id, bucket.Profile.GroupId);
            var saved = fixture.Store.Saved.Profiles.Single(item => item.Id == fixture.First.Id);
            Assert.Equal(fixture.Group.Id, saved.GroupId);
            Assert.Equal(ConnectionHealthStatus.Healthy, saved.HealthStatus);
        });
    }

    [Fact]
    public async Task EditingConnectionInvalidatesBucketsAndIgnoresItsOldLookupResult()
    {
        await RunMessagePumpAsync(async _ =>
        {
            await using var fixture = await AccountTreeFixture.CreateAsync();
            var release = new TaskCompletionSource<IReadOnlyList<BucketInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Storage.Buckets = () => release.Task;
            var account = fixture.Account(fixture.First.Id);
            account.Nodes.Clear();
            account.Nodes.Add(new TreeNode("old-bucket") { Tag = new BucketNodeTag(fixture.First, "old-bucket") });
            var lookup = InvokeAccountTask(fixture.Form, "LoadBucketsAsync", fixture.First, account);
            var changed = fixture.First with { Endpoint = "https://changed.example.test" };
            await InvokeAccountTask(fixture.Form, "SaveConfigurationAndRefreshAsync",
                fixture.Store.Saved with { Profiles = [changed, fixture.Second] });
            release.SetResult([new BucketInfo("stale-bucket", null)]);
            await lookup;
            Assert.DoesNotContain(account.Nodes.Cast<TreeNode>(), node => node.Tag is BucketNodeTag);
            Assert.Equal(changed.Endpoint, Assert.IsType<ConnectionProfile>(account.Tag).Endpoint);
            Assert.Equal(ConnectionHealthStatus.Unknown, fixture.Store.Saved.Profiles.First().HealthStatus);
        });
    }

    [Fact]
    public async Task PendingHealthSaveAndMoveAreSerializedWithoutLostChanges()
    {
        await RunMessagePumpAsync(async _ =>
        {
            await using var fixture = await AccountTreeFixture.CreateAsync();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Store.BeforeSave = () => { started.TrySetResult(); return release.Task; };
            var health = InvokeAccountTask(fixture.Form, "RecordConnectionHealthAsync", fixture.First, true);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var move = InvokeAccountTask(fixture.Form, "MoveProfileToGroupAsync", fixture.First, fixture.Group.Id);
            Assert.False(move.IsCompleted);
            release.SetResult();
            await Task.WhenAll(health, move);
            var profile = fixture.Store.Saved.Profiles.Single(item => item.Id == fixture.First.Id);
            Assert.Equal(fixture.Group.Id, profile.GroupId);
            Assert.Equal(ConnectionHealthStatus.Healthy, profile.HealthStatus);
        });
    }

    [Fact]
    public async Task FailedSaveLeavesTreeAndConfigurationUnchanged()
    {
        await RunMessagePumpAsync(async _ =>
        {
            await using var fixture = await AccountTreeFixture.CreateAsync();
            fixture.Store.BeforeSave = () => throw new IOException("isolated save failure");
            var account = fixture.Account(fixture.First.Id);
            await Assert.ThrowsAsync<IOException>(() => InvokeAccountTask(fixture.Form, "ApplyAccountTreeDropAsync", account, fixture.GroupNode, false));
            Assert.Same(fixture.Tree.Nodes[0], account.Parent);
            Assert.Null(Assert.IsType<ConnectionProfile>(account.Tag).GroupId);
            Assert.Null(GetField<IReadOnlyList<ConnectionProfile>>(fixture.Form, "_profiles").Single(item => item.Id == fixture.First.Id).GroupId);
        });
    }

    private static object? InvokeAccountMethod(MainForm form, string method, params object?[] args) =>
        typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args);

    private static Task InvokeAccountTask(MainForm form, string method, params object?[] args) =>
        Assert.IsAssignableFrom<Task>(InvokeAccountMethod(form, method, args));

    private static DragEventArgs DropArgs(TreeView tree, TreeNode source, TreeNode target, bool after)
    {
        var data = new DataObject();
        data.SetData(typeof(TreeNode), source);
        var point = tree.PointToScreen(new Point(target.Bounds.Left + 2, after ? target.Bounds.Bottom - 1 : target.Bounds.Top + 1));
        return new DragEventArgs(data, 0, point.X, point.Y, DragDropEffects.Move, DragDropEffects.None);
    }

    private sealed class AccountTreeFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "s3-account-tree-" + Guid.NewGuid().ToString("N"));
        private readonly PersistentTransferQueue _queue;
        public ConnectionProfile First { get; } = TestAccount("First", 0);
        public ConnectionProfile Second { get; } = TestAccount("Second", 1);
        public ConnectionGroup Group { get; } = new() { Name = "Test group" };
        public RecordingProfileStore Store { get; }
        public FakeStorageProxy Storage { get; }
        public MainForm Form { get; }
        public TreeView Tree => GetField<TreeView>(Form, "_tree");
        public TreeNode GroupNode => Tree.Nodes[0].Nodes.Cast<TreeNode>().Single(node => node.Tag is ConnectionGroup group && group.Id == Group.Id);
        public TreeNode Account(Guid id) => MainFormNodes(Tree.Nodes).Single(node => node.Tag is ConnectionProfile profile && profile.Id == id);

        private AccountTreeFixture()
        {
            Directory.CreateDirectory(_directory);
            Store = new RecordingProfileStore(new([First, Second], [Group]));
            var storage = DispatchProxy.Create<IS3StorageService, FakeStorageProxy>();
            Storage = (FakeStorageProxy)(object)storage;
            _queue = new PersistentTransferQueue(new JsonTransferTaskStore(Path.Combine(_directory, "tasks.json")), new NeverRunExecutor());
            Form = CreateForm(_queue, new TransferRuntimeConfiguration(), new SimpleFileLogger(Path.Combine(_directory, "logs")), Store, storage: storage);
            _ = Form.Handle;
            _ = Tree.Handle;
            SetField(Form, "_profiles", Store.Saved.Profiles);
            SetField(Form, "_profileGroups", Store.Saved.Groups);
            InvokeInstance(Form, "PopulateProfiles");
        }

        public static async Task<AccountTreeFixture> CreateAsync()
        {
            var fixture = new AccountTreeFixture();
            await fixture._queue.InitializeAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            Form.Dispose();
            await _queue.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }

        private static ConnectionProfile TestAccount(string name, int order) => ConnectionProfile.CreatePreset(S3ServiceType.MinIO) with
        {
            Name = name, Endpoint = "https://storage.example.test", AccessKey = "test-access", SecretKey = "test-secret", SortOrder = order
        };

        private static IEnumerable<TreeNode> MainFormNodes(TreeNodeCollection nodes) =>
            nodes.Cast<TreeNode>().SelectMany(node => new[] { node }.Concat(MainFormNodes(node.Nodes)));
    }

    private sealed class RecordingProfileStore(ConnectionProfileConfiguration initial) : IProfileStore
    {
        public ConnectionProfileConfiguration Saved { get; private set; } = initial;
        public Func<Task>? BeforeSave { get; set; }
        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved.Profiles);
        public Task SaveAsync(IReadOnlyCollection<ConnectionProfile> profiles, CancellationToken cancellationToken = default) =>
            SaveConfigurationAsync(Saved with { Profiles = profiles.ToArray() }, cancellationToken);
        public async Task SaveConfigurationAsync(ConnectionProfileConfiguration configuration, CancellationToken cancellationToken = default)
        {
            if (BeforeSave is not null) await BeforeSave();
            Saved = configuration;
        }
    }
}
