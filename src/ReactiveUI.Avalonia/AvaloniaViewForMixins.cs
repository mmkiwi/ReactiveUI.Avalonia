// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ReactiveUI.Avalonia;

/// <summary>
/// Provides extension methods for registering activation logic on views and view models that support activation. These
/// methods enable the execution of custom code when a view or view model is activated or deactivated, facilitating
/// resource management and lifecycle handling in reactive UI scenarios.
/// </summary>
/// <remarks>The methods in this class are typically used to register disposables or cleanup actions that should
/// be tied to the activation lifecycle of a view or view model. This helps ensure that resources such as subscriptions
/// are properly disposed of when the view is deactivated. Some methods accept an optional view parameter for advanced
/// scenarios where the view and view model are not hosted together. Use these methods to simplify activation-aware
/// resource management in MVVM architectures. Thread safety and correct disposal are managed internally. For unit
/// testing purposes, the cache used to optimize activation fetcher lookups can be reset using the provided internal
/// method; this should not be used in production code.</remarks>
public static class AvaloniaViewForMixins
{
    private static readonly MemoizingMRUCache<Type, IActivationForViewFetcher?> _activationFetcherCache =
        new(
            (t, _) =>
                AppLocator.Current
                    .GetServices<IActivationForViewFetcher?>()
                    .Aggregate((count: 0, viewFetcher: default(IActivationForViewFetcher?)), (acc, x) =>
                    {
                        var score = x?.GetAffinityForView(t) ?? 0;
                        return score > acc.count ? (score, x) : acc;
                    }).viewFetcher,
            RxCacheSize.SmallCacheLimit);

    /// <summary>
    /// Registers a block of disposables to be activated and disposed in sync with the activation lifecycle of the
    /// specified view or view model.
    /// </summary>
    /// <remarks>This method is typically used to manage subscriptions or other resources that should only be
    /// active while the view or view model is active. The activation lifecycle is determined by the implementation of
    /// <see cref="IActivatableView"/> and any registered activation fetchers.</remarks>
    /// <param name="item">The view or view model that implements <see cref="IActivatableView"/> whose activation lifecycle will control
    /// the activation and disposal of the provided disposables. Cannot be null.</param>
    /// <param name="viewModelProperty">The avalonia ViewModel StyledProperty.</param>
    /// <returns>An <see cref="IDisposable"/> that deactivates and disposes the registered resources when disposed. Disposing
    /// this object will also unsubscribe from the activation lifecycle.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="item"/> is null or if activation cannot be determined for the specified type.</exception>
    /// <typeparam name="TView">The view type (typically ReactiveWindow or ReactiveUserControl).</typeparam>
    /// <typeparam name="TViewModel">The viewmodel type.</typeparam>
    public static IDisposable EnsureActivated<TView, TViewModel>(this TView item, StyledProperty<TViewModel?> viewModelProperty) // TODO: Create Test
        where TView : Control, IActivatableView, IViewFor<TViewModel>
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(item);

        var activationFetcher = _activationFetcherCache.Get(item.GetType());
        if (activationFetcher is null)
        {
            const string msg = "Don't know how to detect when {0} is activated/deactivated, you may need to implement IActivationForViewFetcher";
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, msg, item.GetType().FullName));
        }

        var activationEvents = activationFetcher.GetActivationForView(item);

        var vmDisposable = Disposable.Empty;
        vmDisposable = HandleViewModelActivation(item, activationEvents, viewModelProperty);

        var viewDisposable = HandleViewActivation(activationEvents);
        return new CompositeDisposable(vmDisposable, viewDisposable);
    }

    /// <summary>
    /// Manages the activation and deactivation lifecycle of a view by subscribing to an activation observable and
    /// invoking a resource allocation block when activated.
    /// </summary>
    /// <remarks>The block is invoked each time the activation observable emits <see langword="true"/>. Any
    /// disposables created by a previous activation are disposed before the block is invoked again. This method is
    /// typically used to manage resources that should only be active while the view is active.</remarks>
    /// <param name="activation">An observable sequence that signals activation state changes. Emits <see langword="true"/> to indicate
    /// activation and <see langword="false"/> to indicate deactivation.</param>
    /// <returns>A <see cref="System.Reactive.Disposables.CompositeDisposable"/> that manages the subscription to the activation observable and the
    /// disposables created by the block. Disposing this object cleans up all associated resources.</returns>
    private static CompositeDisposable HandleViewActivation(IObservable<bool> activation)
    {
        var viewDisposable = new SerialDisposable();

        return new CompositeDisposable(
                                       activation.Subscribe(activated =>
                                       {
                                           // NB: We need to make sure to respect ordering so that the clean up
                                           // happens before we invoke block again
                                           viewDisposable.Disposable = Disposable.Empty;
                                           if (activated)
                                           {
                                               viewDisposable.Disposable = new CompositeDisposable();
                                           }
                                       }),
                                       viewDisposable);
    }

    /// <summary>
    /// Manages the activation and deactivation lifecycle of a view's ViewModel in response to an activation observable.
    /// </summary>
    /// <remarks>This method subscribes to changes in the view's ViewModel and manages the activation state of
    /// any IActivatableViewModel assigned to the view. It is intended to be used internally to coordinate activation
    /// and deactivation in reactive UI scenarios. The method uses reflection to evaluate expression-based member
    /// chains, which may be affected by trimming in some deployment scenarios.</remarks>
    /// <param name="view">The view implementing the IViewFor interface whose ViewModel activation lifecycle will be managed. Cannot be
    /// null.</param>
    /// <param name="activation">An observable sequence that signals when the view is activated or deactivated. Emits <see langword="true"/> to
    /// indicate activation and <see langword="false"/> for deactivation.</param>
    /// <param name="viewModelProperty">The avalonia ViewModel StyledProperty.</param>
    /// <returns>A CompositeDisposable that manages all subscriptions and resources related to the activation lifecycle.
    /// Disposing this object will clean up all associated subscriptions.</returns>
    private static CompositeDisposable HandleViewModelActivation<TView, TViewModel>(TView view, IObservable<bool> activation, StyledProperty<TViewModel?> viewModelProperty)
        where TView : Control, IActivatableView, IViewFor
        where TViewModel : class
    {
        var vmDisposable = new SerialDisposable();
        var viewVmDisposable = new SerialDisposable();

        return new CompositeDisposable(
                                       activation.Subscribe(activated =>
                                       {
                                           if (activated)
                                           {
                                               viewVmDisposable.Disposable =
                                                   view.GetObservable(viewModelProperty)
                                                   .Select(x => x as IActivatableViewModel)
                                                   .Subscribe(x =>
                                                   {
                                                       // NB: We need to make sure to respect ordering so that the clean up
                                                       // happens before we activate again
                                                       vmDisposable.Disposable = Disposable.Empty;
                                                       if (x is not null)
                                                       {
                                                           vmDisposable.Disposable = x.Activator.Activate();
                                                       }
                                                   });
                                           }
                                           else
                                           {
                                               viewVmDisposable.Disposable = Disposable.Empty;
                                               vmDisposable.Disposable = Disposable.Empty;
                                           }
                                       }),
                                       vmDisposable,
                                       viewVmDisposable);
    }
}
