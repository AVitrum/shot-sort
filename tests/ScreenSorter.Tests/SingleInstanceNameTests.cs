using ScreenSorter.Infrastructure.Win32;

namespace ScreenSorter.Tests;

public sealed class SingleInstanceNameTests
{
    [Fact]
    public void No_argument_keeps_the_installed_apps_name() =>
        Assert.Equal(SingleInstanceName.Default, SingleInstanceName.FromArgs(["--Paths:SortedRoot=C:\\x"]));

    [Theory]
    [InlineData("--App:InstanceName=e2e")]
    [InlineData("--app:instancename=e2e")]
    public void Equals_form_appends_the_name(string arg) =>
        Assert.Equal(SingleInstanceName.Default + ".e2e", SingleInstanceName.FromArgs([arg]));

    [Fact]
    public void Separate_value_form_appends_the_name() =>
        Assert.Equal(SingleInstanceName.Default + ".run-2_a", SingleInstanceName.FromArgs(["--App:InstanceName", "run-2_a"]));

    [Theory]
    [InlineData("--App:InstanceName=")]
    [InlineData("--App:InstanceName=..\\Global\\x")]
    [InlineData("--App:InstanceName=a b")]
    [InlineData("--App:InstanceName")]
    public void Bad_or_missing_name_falls_back_to_the_default(string arg) =>
        Assert.Equal(SingleInstanceName.Default, SingleInstanceName.FromArgs([arg]));

    [Fact]
    public void Overlong_name_falls_back_to_the_default() =>
        Assert.Equal(SingleInstanceName.Default, SingleInstanceName.FromArgs(["--App:InstanceName=" + new string('a', 65)]));
}
