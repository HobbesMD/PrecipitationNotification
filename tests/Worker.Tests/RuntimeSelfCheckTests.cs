namespace Worker.Tests;

public class RuntimeSelfCheckTests
{
    [Fact]
    public void Self_check_passes_on_the_dev_host_and_names_the_zone()
    {
        var output = new StringWriter();

        var exit = RuntimeSelfCheck.Run(output);

        Assert.Equal(0, exit);
        Assert.Contains("America/New_York", output.ToString());
    }
}
