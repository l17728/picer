using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Vision;

namespace Picer.App.Views;

public partial class PersonDetailWindow : Window
{
    private readonly PicerDatabase _database;
    private readonly PeopleService _people;
    private readonly VisionAnalysisRepository _faces;
    private long _personId;

    public PersonDetailWindow(long personId, PicerDatabase database, PeopleService people, VisionAnalysisRepository faces)
    {
        InitializeComponent(); _personId = personId; _database = database; _people = people; _faces = faces;
        Loaded += async (_, _) => await RefreshAsync();
    }

    public bool PeopleChanged { get; private set; }

    private async Task RefreshAsync()
    {
        var snapshot = await Task.Run(() =>
        {
            var person = _people.Get(_personId);
            if (person is null) return null;
            var images = _people.GetImageIds(_personId).Select(_database.GetImage).Where(x => x is not null).Cast<ImageRecord>()
                .OrderBy(SceneTimeClusterService.EffectiveTime).ThenBy(x => x.Id).ToArray();
            var clusters = new SceneTimeClusterService(_database).Build(images).ToArray();
            var targets = _people.GetAll().Where(x => x.Id != _personId).ToArray();
            return new { Person = person, Images = images, Clusters = clusters, Targets = targets };
        });
        if (snapshot is null) { Close(); return; }
        var person = snapshot.Person;
        var images = snapshot.Images;
        Heading.Text = person.DisplayName; NameBox.Text = person.DisplayName;
        DescriptionBox.Text = person.Description;
        Summary.Text = $"{images.Length} 张照片；双击照片可逐脸确认或纠错";
        Aliases.Text = "别名/关系：" + (person.Aliases.Count == 0 ? "无" : string.Join("、", person.Aliases));
        var byId = images.ToDictionary(image => image.Id);
        var groups = new List<PersonSceneGroup>
        {
            new("全部照片", images.Select(image => new PersonPhotoItem(image)).ToArray(), null)
        };
        foreach (var cluster in snapshot.Clusters)
        {
            var photos = cluster.ImageIds.Where(byId.ContainsKey).Select(id => new PersonPhotoItem(byId[id])).ToArray();
            groups.Add(new PersonSceneGroup(cluster.DisplayName, photos, cluster));
        }
        SceneGroups.ItemsSource = groups;
        SceneGroups.SelectedIndex = 0;
        MergeTarget.ItemsSource = snapshot.Targets;
    }

    private void SceneGroups_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SceneGroups.SelectedItem is PersonSceneGroup group) Photos.ItemsSource = group.Photos;
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        try { var name = NameBox.Text; await Task.Run(() => _people.Rename(_personId, name)); PeopleChanged = true; Status.Text = "人物名称已更新。"; await RefreshAsync(); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void AddAlias_Click(object sender, RoutedEventArgs e)
    {
        try { var alias = AliasBox.Text; await Task.Run(() => _people.AddAlias(_personId, alias)); AliasBox.Clear(); PeopleChanged = true; Status.Text = "别名已加入检索。"; await RefreshAsync(); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void SaveDescription_Click(object sender, RoutedEventArgs e)
    {
        try { var description = DescriptionBox.Text; await Task.Run(() => _people.SetDescription(_personId, description)); PeopleChanged = true; Status.Text = "人物描述已保存。"; }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (MergeTarget.SelectedItem is not PersonSummary target) { Status.Text = "请选择合并目标。"; return; }
        if (MessageBox.Show($"将“{Heading.Text}”的全部已确认人脸和别名合并到“{target.DisplayName}”。当前人物条目随后移除。是否继续？",
                "确认合并人物", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { await Task.Run(() => _people.Merge(_personId, target.Id)); _personId = target.Id; PeopleChanged = true; Status.Text = "人物已合并，可在照片中继续逐脸纠错。"; await RefreshAsync(); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void Photos_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Photos.SelectedItem is not PersonPhotoItem item || !File.Exists(item.Record.Path)) return;
        new FaceDetailWindow(item.Record, _faces, _people) { Owner = this }.ShowDialog();
        PeopleChanged = true; await RefreshAsync();
    }

    private sealed class PersonPhotoItem
    {
        public PersonPhotoItem(ImageRecord record) => Record = record;
        public ImageRecord Record { get; }
        public string ThumbPath => File.Exists(Record.ThumbPath) ? Record.ThumbPath! : Record.Path;
        public string FileName => Path.GetFileName(Record.Path);
        public string TimeText => (Record.TakenUtc ?? Record.CreatedUtc ?? Record.ModifiedUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private sealed class PersonSceneGroup(string displayName, IReadOnlyList<PersonPhotoItem> photos, SceneTimeCluster? cluster)
    {
        public string DisplayName { get; } = displayName;
        public IReadOnlyList<PersonPhotoItem> Photos { get; } = photos;
        public string Summary { get; } = cluster is null
            ? $"{photos.Count} 张照片"
            : $"{photos.Count} 张照片 · 至 {cluster.EndUtc.ToLocalTime():MM-dd HH:mm}";
    }
}
