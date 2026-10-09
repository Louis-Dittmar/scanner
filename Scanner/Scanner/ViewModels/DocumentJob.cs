using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Scanner.Core.Imaging;
using Scanner.Core.Ocr;
using Scanner.Core.Pipeline;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.UI;

namespace Scanner.ViewModels;

public enum DocumentJobState
{
    Preparing,
    NeedsSetup,
    WaitingForModel,
    Processing,
    Saving,
    Done,
    Failed,
    Cancelled,
}

public enum DocumentJobStepState
{
    Pending,
    Active,
    Done,
    Failed,
}

/// <summary>
/// One document on its way from the scan to the finished PDF, as shown in the document window. Owned by
/// <see cref="Services.Interfaces.IDocumentPipelineService"/>; only changed on the UI thread.
/// </summary>
public partial class DocumentJob : ObservableObject
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsDone), nameof(IsFailed), nameof(NeedsSetup), nameof(CanReprocess))]
    private DocumentJobState state = DocumentJobState.Preparing;

    [ObservableProperty]
    private string statusText = "";

    /// <summary>
    /// 0..1, <see langword="null"/> while the progress is unknown (e.g. while the model loads).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate), nameof(ProgressPercent))]
    private double? progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? errorText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput), nameof(OutputFolderPath), nameof(OutputFileName))]
    private string? outputPdfPath;

    [ObservableProperty]
    private string? outputMarkdownPath;

    [ObservableProperty]
    private string markdown = "";

    public ObservableCollection<DocumentJobStep> Steps { get; }
    public ObservableCollection<DocumentJobPage> Pages { get; } = [];

    public bool IsBusy => State is DocumentJobState.Preparing or DocumentJobState.WaitingForModel or DocumentJobState.Processing or DocumentJobState.Saving;
    public bool IsDone => State == DocumentJobState.Done;
    public bool IsFailed => State == DocumentJobState.Failed;
    public bool NeedsSetup => State == DocumentJobState.NeedsSetup;
    public bool CanReprocess => !IsBusy && Pages.Count > 0;
    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool HasOutput => !string.IsNullOrEmpty(OutputPdfPath);
    public string? OutputFolderPath => OutputPdfPath == null ? null : System.IO.Path.GetDirectoryName(OutputPdfPath);
    public string? OutputFileName => OutputPdfPath == null ? null : System.IO.Path.GetFileName(OutputPdfPath);
    public bool IsProgressIndeterminate => Progress == null;
    public double ProgressPercent => (Progress ?? 0) * 100;

    #region Steps
    public DocumentJobStep StepLoad => Steps[0];
    public DocumentJobStep StepWhite => Steps[1];
    public DocumentJobStep StepRecognize => Steps[2];
    public DocumentJobStep StepPdf => Steps[3];
    public DocumentJobStep StepSave => Steps[4];
    #endregion


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public DocumentJob(string title)
    {
        Title = title;
        Steps =
        [
            new DocumentJobStep("", Resources.Strings.Resources.DocumentStepLoad),
            new DocumentJobStep("", Resources.Strings.Resources.DocumentStepWhite),
            new DocumentJobStep("", Resources.Strings.Resources.DocumentStepRecognize),
            new DocumentJobStep("", Resources.Strings.Resources.DocumentStepPdf),
            new DocumentJobStep("", Resources.Strings.Resources.DocumentStepSave),
        ];
        Pages.CollectionChanged += (s, e) => OnPropertyChanged(nameof(CanReprocess));
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// Marks all steps before <paramref name="step"/> as done, <paramref name="step"/> as active and the rest as pending.
    /// </summary>
    public void SetActiveStep(DocumentJobStep step)
    {
        int index = Steps.IndexOf(step);
        for (int i = 0; i < Steps.Count; i++)
            Steps[i].State = i < index ? DocumentJobStepState.Done : i == index ? DocumentJobStepState.Active : DocumentJobStepState.Pending;
    }

    public void CompleteAllSteps()
    {
        foreach (DocumentJobStep step in Steps)
            step.State = DocumentJobStepState.Done;
    }

    public void FailActiveStep()
    {
        foreach (DocumentJobStep step in Steps.Where(s => s.State == DocumentJobStepState.Active))
            step.State = DocumentJobStepState.Failed;
    }
}

/// <summary>
/// One of the five steps shown at the top of the document window.
/// </summary>
public partial class DocumentJobStep : ObservableObject
{
    public string Glyph { get; }
    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsDone), nameof(IsFailed), nameof(Opacity), nameof(StateGlyph))]
    private DocumentJobStepState state = DocumentJobStepState.Pending;

    public bool IsActive => State == DocumentJobStepState.Active;
    public bool IsDone => State == DocumentJobStepState.Done;
    public bool IsFailed => State == DocumentJobStepState.Failed;
    public double Opacity => State == DocumentJobStepState.Pending ? 0.45 : 1.0;

    /// <summary>
    /// The step's own icon, a check mark once done or a cross if it failed.
    /// </summary>
    public string StateGlyph => State switch
    {
        DocumentJobStepState.Done => "",
        DocumentJobStepState.Failed => "",
        _ => Glyph,
    };

    public DocumentJobStep(string glyph, string title)
    {
        Glyph = glyph;
        Title = title;
    }
}

/// <summary>
/// A page in the document window: the scan, its white-corrected version and what was recognized on it.
/// </summary>
public partial class DocumentJobPage : ObservableObject
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public int PageNumber { get; }

    /// <summary>
    /// The pipeline's state of this page; replaced when the document is processed again.
    /// </summary>
    public ProcessedPage Page { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedImage))]
    private ImageSource? originalImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedImage), nameof(HasProcessedImage))]
    private ImageSource? processedImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedImage))]
    private bool showOriginal;

    /// <summary>
    /// Size of the displayed images in their own pixels, the overlay uses the same coordinates.
    /// </summary>
    [ObservableProperty]
    private double displayWidth = 1;

    [ObservableProperty]
    private double displayHeight = 1;

    [ObservableProperty]
    private string stepText = "";

    [ObservableProperty]
    private string detectionText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecognizing))]
    private ProcessingStep step = ProcessingStep.Waiting;

    [ObservableProperty]
    private string markdown = "";

    public ObservableCollection<DocumentJobRegion> Regions { get; } = [];

    public ImageSource? DisplayedImage => ShowOriginal || ProcessedImage == null ? OriginalImage : ProcessedImage;
    public bool HasProcessedImage => ProcessedImage != null;
    public bool IsRecognizing => Step == ProcessingStep.Recognizing;


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public DocumentJobPage(ProcessedPage page)
    {
        Page = page;
        PageNumber = page.Index + 1;
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public void SetRegions(OcrPageResult? result)
    {
        Regions.Clear();
        if (result == null)
            return;

        int number = 1;
        foreach (OcrRegion region in result.Regions)
            Regions.Add(new DocumentJobRegion(region, number++, DisplayWidth, DisplayHeight));
    }
}

/// <summary>
/// A recognized area, positioned in the display coordinates of its page.
/// </summary>
public partial class DocumentJobRegion : ObservableObject
{
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // DECLARATIONS /////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public OcrRegion Region { get; }
    public int Number { get; }
    public string Label => Region.Label;
    public string KindText { get; }
    public string Text { get; }
    public string Preview { get; }
    public Thickness Position { get; }
    public double Width { get; }
    public double Height { get; }
    public SolidColorBrush Stroke { get; }
    public SolidColorBrush Fill { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrokeThickness))]
    private bool isHighlighted;

    public Thickness StrokeThickness => new(IsHighlighted ? 5 : 2);


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // CONSTRUCTORS / FACTORIES /////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public DocumentJobRegion(OcrRegion region, int number, double displayWidth, double displayHeight)
    {
        Region = region;
        Number = number;
        KindText = GetKindText(region.Kind);
        Text = OcrText.ToPlainText(region.Text);
        Preview = Text.Length > 140 ? Text[..140].TrimEnd() + "…" : Text;

        NormalizedBox box = region.Box;
        Position = new Thickness(box.X1 / 1000.0 * displayWidth, box.Y1 / 1000.0 * displayHeight, 0, 0);
        Width = Math.Max(2, box.Width / 1000.0 * displayWidth);
        Height = Math.Max(2, box.Height / 1000.0 * displayHeight);

        Color color = GetColor(region.Kind);
        Stroke = new SolidColorBrush(color);
        Fill = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
    }


    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // METHODS //////////////////////////////////////////////////////////////////////////////////////////////////////////////
    /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public static Color GetColor(OcrRegionKind kind) => kind switch
    {
        OcrRegionKind.Title => ColorHelper.FromArgb(255, 0xE8, 0x59, 0x0C),
        OcrRegionKind.Table => ColorHelper.FromArgb(255, 0x2F, 0x9E, 0x44),
        OcrRegionKind.Image => ColorHelper.FromArgb(255, 0xAE, 0x3E, 0xC9),
        OcrRegionKind.Formula => ColorHelper.FromArgb(255, 0xF5, 0x9F, 0x00),
        OcrRegionKind.List => ColorHelper.FromArgb(255, 0x10, 0x98, 0xAD),
        OcrRegionKind.Caption or OcrRegionKind.PageHeader or OcrRegionKind.PageFooter => ColorHelper.FromArgb(255, 0x86, 0x8E, 0x96),
        _ => ColorHelper.FromArgb(255, 0x1C, 0x7E, 0xD6),
    };

    public static string GetKindText(OcrRegionKind kind) => kind switch
    {
        OcrRegionKind.Title => Resources.Strings.Resources.RegionKindTitle,
        OcrRegionKind.Table => Resources.Strings.Resources.RegionKindTable,
        OcrRegionKind.Image => Resources.Strings.Resources.RegionKindImage,
        OcrRegionKind.Formula => Resources.Strings.Resources.RegionKindFormula,
        OcrRegionKind.List => Resources.Strings.Resources.RegionKindList,
        OcrRegionKind.Caption => Resources.Strings.Resources.RegionKindCaption,
        OcrRegionKind.PageHeader => Resources.Strings.Resources.RegionKindHeader,
        OcrRegionKind.PageFooter => Resources.Strings.Resources.RegionKindFooter,
        _ => Resources.Strings.Resources.RegionKindText,
    };
}
