using System.Windows;
using System.Windows.Controls;
using RecipeSong.Wpf.ViewModels;

namespace RecipeSong.Wpf;

public partial class RecommendationEvidenceView : UserControl
{
    public RecommendationEvidenceView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Body.DataContext = e.NewValue is ReleaseItem item ? new RecommendationEvidence(item.Rec) : null;
    }
}
