namespace RxNavigation
{
    public interface IPageViewModel
    {
        string Title { get; }
    }

    public interface ITabPageViewModel : IPageViewModel
    {
    }
}
