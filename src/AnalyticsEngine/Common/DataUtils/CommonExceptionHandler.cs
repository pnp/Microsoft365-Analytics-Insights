using System;

namespace Common.DataUtils
{
    public class CommonExceptionHandler
    {
        public static string GetErrorText(Exception ex)
        {
            if (HasInnerException(ex))
            {
                return ex.Message + "; " + GetErrorText(ex.InnerException);
            }
            else
            {
                return ex.Message;
            }
        }

        static bool HasInnerException(Exception ex)
        {
            if (ex.InnerException != null)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public static Exception GetRootException(Exception ex)
        {
            if (ex.InnerException != null)
            {
                return GetRootException(ex.InnerException);
            }
            else
            {
                return ex;
            }
        }
    }
}
