using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Vision;

namespace Picer.App.Views;

public partial class FaceDetailWindow : Window
{
    private readonly ImageRecord _image;
    private readonly VisionAnalysisRepository _repository;
    private readonly PeopleService _people;
    private readonly long? _initialFaceId;
    private readonly Dictionary<long, Rectangle> _rectangles = [];

    public FaceDetailWindow(ImageRecord image, VisionAnalysisRepository repository, PeopleService people, long? initialFaceId = null)
    {
        InitializeComponent();
        _image = image;
        _repository = repository;
        _people = people;
        _initialFaceId = initialFaceId;
        LoadPhoto();
        RefreshFaces();
        PersonPicker.ItemsSource = people.GetAll();
    }

    private void LoadPhoto()
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 1600;
        bitmap.UriSource = new Uri(_image.Path);
        bitmap.EndInit();
        bitmap.Freeze();
        Photo.Source = bitmap;
        ImageStage.Width = bitmap.PixelWidth;
        ImageStage.Height = bitmap.PixelHeight;
        FaceCanvas.Width = bitmap.PixelWidth;
        FaceCanvas.Height = bitmap.PixelHeight;
    }

    private void RefreshFaces()
    {
        var faces = _repository.GetFaces(_image.Id).Select(face => new FaceDetailItem(face)).ToArray();
        FaceList.ItemsSource = faces;
        FaceCanvas.Children.Clear();
        _rectangles.Clear();
        foreach (var item in faces)
        {
            var box = item.Record.Box;
            var rectangle = new Rectangle
            {
                Width = box.Width * ImageStage.Width,
                Height = box.Height * ImageStage.Height,
                Stroke = item.Record.AssignmentState == "confirmed" ? Brushes.LimeGreen : Brushes.Orange,
                StrokeThickness = Math.Max(2, ImageStage.Width / 600),
                Fill = Brushes.Transparent,
                ToolTip = item.IdentityText
            };
            rectangle.MouseLeftButtonDown += (_, _) => FaceList.SelectedItem = item;
            Canvas.SetLeft(rectangle, box.X * ImageStage.Width);
            Canvas.SetTop(rectangle, box.Y * ImageStage.Height);
            FaceCanvas.Children.Add(rectangle);
            _rectangles[item.Record.FaceId] = rectangle;
        }
        if (faces.Length > 0)
            FaceList.SelectedItem = faces.FirstOrDefault(face => face.Record.FaceId == _initialFaceId) ?? faces[0];
    }

    private void FaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var rectangle in _rectangles.Values) rectangle.StrokeThickness = Math.Max(2, ImageStage.Width / 600);
        if (FaceList.SelectedItem is FaceDetailItem item && _rectangles.TryGetValue(item.Record.FaceId, out var selected))
            selected.StrokeThickness = Math.Max(5, ImageStage.Width / 250);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (FaceList.SelectedItem is not FaceDetailItem face || PersonPicker.SelectedItem is not PersonSummary person)
        {
            Status.Text = "请选择人脸和人物。";
            return;
        }
        _people.ConfirmFace(face.Record.FaceId, person.Id);
        Status.Text = $"已确认为“{person.DisplayName}”。";
        RefreshFaces();
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        if (FaceList.SelectedItem is not FaceDetailItem face || face.Record.PersonId is not long personId)
        {
            Status.Text = "所选人脸当前没有人物归属或建议。";
            return;
        }
        _people.MarkNotThisPerson(face.Record.FaceId, personId);
        Status.Text = "已移除错误归属，并记录拒绝约束。";
        RefreshFaces();
    }

    private sealed class FaceDetailItem(ImageFaceRecord record)
    {
        public ImageFaceRecord Record { get; } = record;
        public int DisplayOrdinal => Record.Ordinal + 1;
        public string IdentityText => Record.PersonName is null ? "未归类" :
            Record.AssignmentState == "confirmed" ? $"已确认：{Record.PersonName}" : $"可能是：{Record.PersonName}";
        public string ConfidenceText => $"检测置信度 {Record.Confidence:F2}";
    }
}
