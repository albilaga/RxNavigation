using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Splat;

namespace RxNavigation
{
    public interface IViewStackService
    {
        IView View { get; }

        IObservable<IImmutableList<IPageViewModel>> PageStack { get; }

        IObservable<IImmutableList<IPageViewModel>> ModalStack { get; }

        IObservable<Unit> PushPage(
            IPageViewModel page,
            string contract = null,
            bool resetStack = false,
            bool animate = true);

        IObservable<Unit> PushAndDestroyLastPage(
            IPageViewModel page,
            bool animate = true);

        void InsertPage(
            int index,
            IPageViewModel page,
            string contract = null);

        IObservable<Unit> PopToPage(
            int index,
            bool animateLastPage = true);

        IObservable<Unit> PopPages(
            int count = 1,
            bool animateLastPage = true);

        IObservable<Unit> PopToRoot(
           bool animateLastPage = true);


        IObservable<Unit> PushModal(
            IPageViewModel modal,
            string contract = null);

        IObservable<Unit> PushModal(
            INavigationPageViewModel modal,
            string contract = null);

        IObservable<Unit> PopModal();
    }

    public sealed class ViewStackService : IViewStackService, IEnableLogger
    {
        private readonly BehaviorSubject<IImmutableList<IPageViewModel>> _ModalPageStack;
        private readonly BehaviorSubject<IImmutableList<IPageViewModel>> _DefaultNavigationStack;
        private readonly BehaviorSubject<IImmutableList<IPageViewModel>> _NullPageStack;

        private BehaviorSubject<IImmutableList<IPageViewModel>> _CurrentPageStack;
        private readonly bool _IsAndroid;

        public ViewStackService(IView view, bool isAndroid)
        {
            this._IsAndroid = isAndroid;
            this.View = view ?? throw new NullReferenceException("The view can't be null.");

            this._ModalPageStack = new BehaviorSubject<IImmutableList<IPageViewModel>>(ImmutableList<IPageViewModel>.Empty);
            this._DefaultNavigationStack = new BehaviorSubject<IImmutableList<IPageViewModel>>(ImmutableList<IPageViewModel>.Empty);
            this._NullPageStack = new BehaviorSubject<IImmutableList<IPageViewModel>>(null);
            this._CurrentPageStack = new BehaviorSubject<IImmutableList<IPageViewModel>>(ImmutableList<IPageViewModel>.Empty);

            this
                ._ModalPageStack
                    .Select(
                        x =>
                        {
                            if (x.Count > 0)
                            {
                                if (x[x.Count - 1] is INavigationPageViewModel navigationPage)
                                {
                                    return navigationPage.PageStack;
                                }
                                else
                                {
                                    return _NullPageStack;
                                }
                            }
                            else
                            {
                                return this._DefaultNavigationStack;
                            }
                        })
                    .Subscribe(x => _CurrentPageStack = x);

            this
                .View
                .PagePopped
                .Do(
                    pageVm =>
                    {
                        var stack = _CurrentPageStack.Value;

                        if (stack.Count == 0)
                        {
                            return;
                        }

                        var removedItem = stack[stack.Count - 1];
                        if (!pageVm.Equals(removedItem))
                        {
                            return;
                        }
                        var removedPage = PopStackAndTick(_CurrentPageStack);
                        this.Log().Debug("Removed page '{0}' from stack.", removedPage.Title);
                    })
                .Subscribe();

            this
                .View
                .ModalPopped
                .Do(
                    _ =>
                    {
                        var removedPage = PopStackAndTick(_ModalPageStack);
                        this.Log().Debug("Removed modal page '{0}' from stack.", removedPage.Title);
                    })
                .Subscribe();
        }

        public IView View { get; }

        public IObservable<IImmutableList<IPageViewModel>> PageStack => _CurrentPageStack;

        public IObservable<IImmutableList<IPageViewModel>> ModalStack => _ModalPageStack;

        public IObservable<Unit> PushPage(IPageViewModel page, string contract = null, bool resetStack = false, bool animate = true)
        {
            if (this._CurrentPageStack.Value == null)
            {
                throw new InvalidOperationException("Can't push a page onto a modal with no navigation stack.");
            }
            return
                View
                .PushPage(page, contract, resetStack, animate)
                .Do(
                    _ =>
                    {
                        AddToStackAndTick(this._CurrentPageStack, page, resetStack);
                        this.Log().Debug("Added page '{0}' (contract '{1}') to stack.", page.Title, contract);
                    });
        }

        public void InsertPage(int index, IPageViewModel page, string contract = null)
        {
            if (page == null)
            {
                throw new NullReferenceException("The page you tried to insert is null.");
            }

            var stack = _CurrentPageStack.Value;

            if (stack == null)
            {
                throw new InvalidOperationException("Can't insert a page into a modal with no navigation stack.");
            }

            if (index < 0 || index >= stack.Count)
            {
                throw new IndexOutOfRangeException(string.Format("Tried to insert a page at index {0}. Stack count: {1}", index, stack.Count));
            }

            stack = stack.Insert(index, page);
            _CurrentPageStack.OnNext(stack);
            View.InsertPage(index, page, contract);
        }

        public IObservable<Unit> PopToPage(int index, bool animateLastPage = true)
        {
            var stack = _CurrentPageStack.Value;

            if (stack == null)
            {
                throw new InvalidOperationException("Can't pop a page from a modal with no navigation stack.");
            }

            if (index < 0 || index >= stack.Count)
            {
                throw new IndexOutOfRangeException(string.Format("Tried to pop to page at index {0}. Stack count: {1}", index, stack.Count));
            }

            int idxOfLastPage = stack.Count - 1;
            int numPagesToPop = idxOfLastPage - index;

            return PopPages(numPagesToPop, animateLastPage);
        }

        public IObservable<Unit> PopToRoot(bool animateLastPage = true)
        {
            var stack = _CurrentPageStack.Value;

            if (stack == null)
            {
                throw new InvalidOperationException("Can't pop a page from a modal with no navigation stack.");
            }

            var idxOfLastPage = stack.Count - 1;
            return PopPages(idxOfLastPage, animateLastPage);
        }

        public IObservable<Unit> PopPages(int count = 1, bool animateLastPage = true)
        {
            var stack = _CurrentPageStack.Value;

            if (stack == null)
            {
                throw new InvalidOperationException("Can't pop pages from a modal with no navigation stack.");
            }

            if (count <= 0 || count >= stack.Count)
            {
                throw new IndexOutOfRangeException(
                    string.Format("Page pop count should be greater than 0 and less than the size of the stack. Pop count: {0}. Stack count: {1}", count, stack.Count));
            }

            if (count > 1)
            {
                // Remove count - 1 pages (leaving the top page).
                var idxOfSecondToLastPage = stack.Count - 2;
                for (var i = idxOfSecondToLastPage; i >= stack.Count - count; --i)
                {
                    View.RemovePage(i);
                }

                stack = stack.RemoveRange(stack.Count - count, count - 1);
                _CurrentPageStack.OnNext(stack);
            }

            // Now remove the top page with optional animation.
            return
                View
                .PopPage(animateLastPage);
        }

        public IObservable<Unit> PushModal(IPageViewModel modal, string contract = null)
        {
            if (modal == null)
            {
                throw new NullReferenceException("The modal you tried to push is null.");
            }

            return
                View
                .PushModal(modal, contract, false)
                .Do(
                    _ =>
                    {
                        AddToStackAndTick(_ModalPageStack, modal, false);
                        this.Log().Debug("Added modal '{0}' (contract '{1}') to stack.", modal.Title, contract);
                    });
        }

        public IObservable<Unit> PushModal(INavigationPageViewModel modal, string contract = null)
        {
            if (modal == null)
            {
                throw new NullReferenceException("The modal you tried to insert is null.");
            }

            if (modal.PageStack.Value.Count <= 0)
            {
                throw new InvalidOperationException("Can't push an empty navigation page.");
            }

            return
                View
                .PushModal(modal.PageStack.Value[0], contract, true)
                .Do(
                    _ =>
                    {
                        AddToStackAndTick(_ModalPageStack, modal, false);
                        this.Log().Debug("Added modal '{0}' (contract '{1}') to stack.", modal.Title, contract);
                    });
        }

        public IObservable<Unit> PopModal()
        {
            return
                View
                .PopModal();
        }

        private static void AddToStackAndTick<T>(BehaviorSubject<IImmutableList<T>> stackSubject, T item, bool reset)
        {
            var stack = stackSubject.Value;

            stack = reset ? new[] { item }.ToImmutableList() : stack.Add(item);

            stackSubject.OnNext(stack);
        }

        private static T PopStackAndTick<T>(BehaviorSubject<IImmutableList<T>> stackSubject)
        {
            var stack = stackSubject.Value;

            if (stack.Count == 0)
            {
                throw new InvalidOperationException("Stack is empty.");
            }

            var removedItem = stack[stack.Count - 1];
            stack = stack.RemoveAt(stack.Count - 1);
            stackSubject.OnNext(stack);
            return removedItem;
        }

        public IObservable<Unit> PushAndDestroyLastPage(IPageViewModel page, bool animate = true)
        {
            if (_CurrentPageStack.Value == null)
            {
                throw new InvalidOperationException("Can't push a page onto a modal with no navigation stack.");
            }
            return
                View
                .PushAndDestroyLastPage(page, animate)
                //.Take(1)
                .Do(
                    _ =>
                    {
                        if (_IsAndroid)
                        {
                            PopStackAndTick(_CurrentPageStack);
                        }
                        AddToStackAndTick(_CurrentPageStack, page, false);
                        this.Log().Debug("Added page '{0}' (contract '{1}') to stack.", page.Title, null);
                    });
        }
    }
}
