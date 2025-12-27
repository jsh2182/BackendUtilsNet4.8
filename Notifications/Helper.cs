using BackendUtils.Models;
using System;
using System.Collections.Generic;
using System.Data.Entity.Validation;
using System.Linq;
using System.Text;


namespace BackendUtils.Notifications
{
    public class Helper
    {
        public static void Add2Log(string formName, string methodName, Exception ex)
        {
            try
            {
                using (BihamtaContext db = new BihamtaContext())
                {
                    string msg = "";
                        while (true)
                        {
                            if (!ex.Message.Contains("See the inner exception for details"))
                            {
                                msg += ex.Message + "\n" + ex.StackTrace + "\n";
                            }

                            if (ex.InnerException == null)
                            {
                                break;
                            }

                            ex = ex.InnerException;
                        }
                    
                    db.ExceptionLog.Add(new BackendUtils.Models.ExceptionLog
                    {
                        FormName = formName,
                        MethodName = methodName,
                        ExcpetionDesc = msg.Replace("The statement has been terminated.", ""),
                        ExceptionDate = DateTime.Now,

                    });

                    db.SaveChanges();
                }
            }
            catch (Exception e)
            {
            }
        }
    }
}
