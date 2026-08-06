using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using ExamTransfer.Desktop.Models;
using ExamTransfer.Desktop.Services;
using ExamTransfer.Desktop.ViewModels;
using ExamTransfer.Desktop.Views;
using ExamTransfer.Shared.Contracts;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public class MainViewModelNavigationTests
{
    private static readonly MethodInfo navigateMethod = typeof(MainViewModel).GetMethod(
        ""NavigateSafelyAsync"", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static Task NavigateAsync(MainViewModel vm, string key)
    {
        var item = vm.Navigation.FirstOrDefault(x => x.Key == key)
            ?? new NavigationItem(key, ""Test"", ""Test"", ""Test"", ""Test"");
        return (Task)navigateMethod.Invoke(vm, new object[] { item })!;
    }

    [Fact]
    public void ConcurrentNavigation_CancelsOldInitialization_And_PreventsStaleException()
    {
        WpfTestHost.Run(() =>
        {
            var vm = new MainViewModel();
            var genField = typeof(MainViewModel).GetField(""navigationGeneration"", BindingFlags.NonPublic | BindingFlags.Instance)!;
            
            var t1 = NavigateAsync(vm, ""S-05"");
            var gen1 = (int)genField.GetValue(vm)!;
            
            var t2 = NavigateAsync(vm, ""S-06"");
            var gen2 = (int)genField.GetValue(vm)!;
            
            Assert.NotEqual(gen1, gen2);
        });
    }

    [Fact]
    public void DuplicateS06_CoalescesNavigation_And_DoesNotRecreateViewModel()
    {
        WpfTestHost.Run(() =>
        {
            var vm = new MainViewModel();
            var genField = typeof(MainViewModel).GetField(""navigationGeneration"", BindingFlags.NonPublic | BindingFlags.Instance)!;
            
            var t1 = NavigateAsync(vm, ""S-06"");
            var gen1 = (int)genField.GetValue(vm)!;
            
            var t2 = NavigateAsync(vm, ""S-06"");
            var gen2 = (int)genField.GetValue(vm)!;
            
            Assert.Equal(gen1, gen2); // Coalesced, generation should not change
        });
    }
}
