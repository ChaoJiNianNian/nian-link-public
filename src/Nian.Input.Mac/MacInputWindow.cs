namespace Nian.Input.Mac;

/// <summary>The current application's explicit native window and its Quartz client rectangle.</summary>
public sealed record MacInputWindow(int Number, double X, double Y, double Width, double Height)
{
    internal bool IsValid => Number > 0 && double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0 &&
        double.IsFinite(X + Width) && double.IsFinite(Y + Height) && X + Width > X && Y + Height > Y;
}
