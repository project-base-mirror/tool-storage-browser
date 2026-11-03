using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed partial class MainForm
{
    private readonly ContextMenuStrip _accountsMenu = new() { Name = "AccountsContextMenu" };
    private readonly ToolStripButton _accountMoveUp = new("↑") { Name = "AccountMoveUp", ToolTipText = "上移选中的账户或分组" };
    private readonly ToolStripButton _accountMoveDown = new("↓") { Name = "AccountMoveDown", ToolTipText = "下移选中的账户或分组" };
    private readonly SemaphoreSlim _profileConfigurationGate = new(1, 1);
    private TreeNode? _draggedAccountNode;
    private TreeNode? _accountDropHighlight;
    private Color _accountDropBackColor;
    private string? _accountDragStatus;
    private long _accountDragScrollAt;

    private ToolStrip BuildAccountToolbar()
    {
        var strip = new ToolStrip { Name = "AccountToolbar", Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
        var add = new ToolStripButton("新建连接", UiIcons.Create(UiIconKind.NewConnection, 16))
        {
            Name = "AccountNewConnection", DisplayStyle = ToolStripItemDisplayStyle.Image,
            ToolTipText = "新建连接（选中分组时添加到该分组）"
        };
        add.Click += async (_, _) => await RunUiCommandAsync("新建连接", NewConnectionAsync);
        var group = new ToolStripButton("新建分组", UiIcons.Create(UiIconKind.Folder, 16)) { Name = "AccountNewGroup" };
        group.Click += async (_, _) => await RunUiCommandAsync("新建连接分组", NewConnectionGroupAsync);
        _accountMoveUp.Click += async (_, _) => await RunUiCommandAsync("上移", () => MoveSelectedAccountTreeItemAsync(-1));
        _accountMoveDown.Click += async (_, _) => await RunUiCommandAsync("下移", () => MoveSelectedAccountTreeItemAsync(1));
        strip.Items.AddRange([add, group, new ToolStripSeparator(), _accountMoveUp, _accountMoveDown]);
        return strip;
    }

    private void BuildAccountsContextMenu()
    {
        _accountsMenu.Items.Add("新建连接...", UiIcons.Create(UiIconKind.NewConnection, 16),
            async (_, _) => await RunUiCommandAsync("新建连接", NewConnectionAsync));
        _accountsMenu.Items.Add("新建连接分组...", UiIcons.Create(UiIconKind.Folder, 16),
            async (_, _) => await RunUiCommandAsync("新建连接分组", NewConnectionGroupAsync));
        _tree.ContextMenuStrip = _accountsMenu;
        _tree.Nodes[0].ToolTipText = "右键或使用上方按钮新建分组；拖入账户可移到未分组。";
    }

    private ContextMenuStrip? AccountTreeContextMenu(TreeNode? node) => node?.Tag switch
    {
        ConnectionProfile => _accountMenu,
        ConnectionGroup => _groupMenu,
        BucketNodeTag => _bucketMenu,
        _ => node is null || node == _tree.Nodes[0] ? _accountsMenu : null
    };

    private void SelectTreeNodeWithoutNavigation(TreeNode node)
    {
        var previous = _suppressTreeSelection;
        _suppressTreeSelection = true;
        try { _tree.SelectedNode = node; }
        finally { _suppressTreeSelection = previous; }
        UpdateCommandStates();
    }

    private Task MoveSelectedAccountTreeItemAsync(int offset) => _tree.SelectedNode?.Tag switch
    {
        ConnectionGroup => MoveSelectedGroupAsync(offset),
        ConnectionProfile => MoveSelectedProfileAsync(offset),
        _ => Task.CompletedTask
    };

    private void UpdateAccountOrderButtons()
    {
        var node = _tree.SelectedNode;
        var siblings = node?.Parent?.Nodes.Cast<TreeNode>()
            .Where(item => node.Tag is ConnectionProfile ? item.Tag is ConnectionProfile : item.Tag is ConnectionGroup)
            .ToArray() ?? [];
        var index = Array.IndexOf(siblings, node);
        var movable = node?.Tag is ConnectionProfile or ConnectionGroup;
        _accountMoveUp.Enabled = movable && index > 0;
        _accountMoveDown.Enabled = movable && index >= 0 && index < siblings.Length - 1;
    }

    private void WireAccountTreeEvents()
    {
        _tree.ItemDrag += (_, args) =>
        {
            if (args.Button != MouseButtons.Left || args.Item is not TreeNode node ||
                node.Tag is not (ConnectionProfile or ConnectionGroup)) return;
            _draggedAccountNode = node;
            _accountDragStatus = _requestStatus.Text;
            try { _tree.DoDragDrop(node, DragDropEffects.Move); }
            finally
            {
                _draggedAccountNode = null;
                ClearAccountDropFeedback();
                _accountDragStatus = null;
            }
        };
        _tree.DragEnter += PreviewAccountTreeDrop;
        _tree.DragOver += PreviewAccountTreeDrop;
        _tree.DragLeave += (_, _) => ClearAccountDropFeedback();
        _tree.DragDrop += async (_, args) =>
        {
            var drop = ResolveAccountTreeDrop(args);
            ClearAccountDropFeedback();
            if (drop is null) return;
            await RunUiCommandAsync("移动账户或分组", () => ApplyAccountTreeDropAsync(drop.Value.Source, drop.Value.Target, drop.Value.After));
        };
    }

    private (TreeNode Source, TreeNode Target, bool After)? ResolveAccountTreeDrop(DragEventArgs args)
    {
        if ((args.AllowedEffect & DragDropEffects.Move) == 0 ||
            args.Data?.GetData(typeof(TreeNode)) is not TreeNode source ||
            source != _draggedAccountNode || source.TreeView != _tree) return null;
        var point = _tree.PointToClient(new Point(args.X, args.Y));
        if (!_tree.ClientRectangle.Contains(point)) return null;
        var target = _tree.GetNodeAt(point) ?? _tree.Nodes[0];
        if (source == target) return null;
        var valid = source.Tag switch
        {
            ConnectionProfile => target.Tag is ConnectionProfile or ConnectionGroup || target == _tree.Nodes[0],
            ConnectionGroup => target.Tag is ConnectionGroup,
            _ => false
        };
        return valid ? (source, target, point.Y >= target.Bounds.Top + target.Bounds.Height / 2) : null;
    }

    private void PreviewAccountTreeDrop(object? sender, DragEventArgs args)
    {
        ClearAccountDropFeedback();
        ScrollAccountTreeDuringDrag(args);
        var drop = ResolveAccountTreeDrop(args);
        args.Effect = drop is null ? DragDropEffects.None : DragDropEffects.Move;
        if (drop is not { } value) return;
        _accountDropHighlight = value.Target;
        _accountDropBackColor = value.Target.BackColor;
        value.Target.BackColor = SystemColors.ControlLight;
        var relative = value.Source.Tag is ConnectionGroup || value.Target.Tag is ConnectionProfile;
        _requestStatus.Text = relative
            ? $"将“{value.Source.Text}”移到“{value.Target.Text}”{(value.After ? "之后" : "之前")}"
            : $"将“{value.Source.Text}”移到{(value.Target.Tag is ConnectionGroup ? $"分组“{value.Target.Text}”" : "未分组")}末尾";
    }

    private void ScrollAccountTreeDuringDrag(DragEventArgs args)
    {
        if (_draggedAccountNode?.TreeView != _tree) return;
        var point = _tree.PointToClient(new Point(args.X, args.Y));
        if (!_tree.ClientRectangle.Contains(point)) return;
        if (Environment.TickCount64 - _accountDragScrollAt < 200) return;
        var next = point.Y < _tree.ItemHeight ? _tree.TopNode?.PrevVisibleNode
            : point.Y > _tree.ClientSize.Height - _tree.ItemHeight ? _tree.TopNode?.NextVisibleNode : null;
        if (next is null) return;
        _tree.TopNode = next;
        _accountDragScrollAt = Environment.TickCount64;
    }

    private void ClearAccountDropFeedback()
    {
        if (_accountDropHighlight is null) return;
        _accountDropHighlight.BackColor = _accountDropBackColor;
        _accountDropHighlight = null;
        if (_accountDragStatus is not null) _requestStatus.Text = _accountDragStatus;
    }

    private async Task ApplyAccountTreeDropAsync(TreeNode source, TreeNode target, bool after)
    {
        if (source == target || source.TreeView != _tree || target.TreeView != _tree) return;
        await UpdateProfileConfigurationAsync(current =>
        {
            var moved = (source.Tag, target.Tag) switch
            {
                (ConnectionProfile profile, ConnectionProfile anchor) => current.PlaceProfileRelative(profile.Id, anchor.Id, after),
                (ConnectionProfile profile, ConnectionGroup group) => current.PlaceProfile(profile.Id, group.Id, int.MaxValue),
                (ConnectionProfile profile, _) when target == _tree.Nodes[0] => current.PlaceProfile(profile.Id, null, int.MaxValue),
                (ConnectionGroup group, ConnectionGroup anchor) => current.PlaceGroupRelative(group.Id, anchor.Id, after),
                _ => current
            };
            return source.Tag is ConnectionProfile account
                ? RevealAccountGroup(moved, account.Id) : moved;
        });
        // Keep an open bucket selected when its account moves. Reveal the new location without navigating.
        source.Parent?.Expand();
        (_tree.SelectedNode ?? source).EnsureVisible();
    }

    private static ConnectionProfileConfiguration RevealAccountGroup(ConnectionProfileConfiguration configuration, Guid profileId)
    {
        var groupId = configuration.Profiles.First(profile => profile.Id == profileId).GroupId;
        return configuration with
        {
            Groups = configuration.Groups.Select(group => group.Id == groupId
                ? group with { IsExpanded = true } : group).ToArray()
        };
    }

    private async Task UpdateProfileConfigurationAsync(
        Func<ConnectionProfileConfiguration, ConnectionProfileConfiguration> update, bool refreshTree = true)
    {
        await _profileConfigurationGate.WaitAsync();
        try
        {
            var current = new ConnectionProfileConfiguration(_profiles, _profileGroups);
            var proposed = update(current).Normalize();
            proposed.Validate();
            if (proposed.Profiles.SequenceEqual(current.Profiles) && proposed.Groups.SequenceEqual(current.Groups)) return;
            await _profileStore.SaveConfigurationAsync(proposed);
            _profiles = proposed.Profiles;
            _profileGroups = proposed.Groups;
            if (_currentProfile is not null)
                _currentProfile = _profiles.FirstOrDefault(profile => profile.Id == _currentProfile.Id);
            if (refreshTree) PopulateProfiles();
        }
        finally { _profileConfigurationGate.Release(); }
    }
}
