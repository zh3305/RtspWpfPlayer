[Serializable]
internal class FFmpegException : Exception
{
    private string v;
    private int result;

    public FFmpegException()
    {
    }

    public FFmpegException(string? message) : base(message)
    {
    }

    public FFmpegException(string v, int result)
    {
        this.v = v;
        this.result = result;
    }

    public FFmpegException(string? message, Exception? innerException) : base(message, innerException)
    {

    }
}