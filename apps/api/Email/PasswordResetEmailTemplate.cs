namespace Taslim.Api.Email;

public sealed record PasswordResetEmail(string Subject, string Body);

public static class PasswordResetEmailTemplate
{
    public static PasswordResetEmail Create(string? language, string resetUrl)
    {
        return language switch
        {
            "ar" => new PasswordResetEmail(
                "إعادة تعيين كلمة مرور Taslim.ai",
                $"تلقينا طلبًا لإعادة تعيين كلمة مرور حسابك في Taslim.ai. اختر كلمة مرور جديدة خلال ساعة باستخدام الرابط التالي:\n\n{resetUrl}\n\nإذا لم تطلب ذلك، فتجاهل هذه الرسالة. لم تتغير كلمة مرورك."),
            "ku" => new PasswordResetEmail(
                "ڕێکخستنەوەی وشەی نهێنی Taslim.ai",
                $"داواکارییەکمان وەرگرتووە بۆ ڕێکخستنەوەی وشەی نهێنی هەژمارەکەت لە Taslim.ai. لە ماوەی یەک کاتژمێردا وشەیەکی نهێنی نوێ هەڵبژێرە بە بەکارهێنانی ئەم بەستەرە:\n\n{resetUrl}\n\nئەگەر ئەم داواکارییەت نەکردووە، ئەم پەیامە پشتگوێ بخە. وشەی نهێنیت نەگۆڕدراوە."),
            _ => new PasswordResetEmail(
                "Reset your Taslim.ai password",
                $"We received a request to reset your Taslim.ai password. Choose a new password within one hour using this link:\n\n{resetUrl}\n\nIf you did not request this, you can ignore this email. Your password has not changed."),
        };
    }
}
