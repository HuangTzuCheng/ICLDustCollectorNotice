using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mail;

namespace ICLDustCollectorNotice
{
    internal class Program
    {
        static void Main(string[] args)
        {
            string title = "機台粉塵清運量記錄[異常通知]";
            string SMTP_SERV = "tchsmtp2.aidc.com.tw";
            string SMTP_PORT = "25";
            string SMTP_FROM = "ICL2@ms.aidc.com.tw";
            string SMTP_FROM_NAME = "粉塵清運量異常通知系統";
            string SMTP_ACC = "";
            string SMTP_PWD = "";
            string TEST_CONTENT = "";
            //string mailto = "tzchenghuang@ms.aidc.com.tw"; // 測試


            
            try
            {
                string sql = "SELECT B.DEPT1 as 單位 FROM ICL2_MAC A LEFT JOIN ICL2_DC_NOTIFY_EMP B ON A.FID = B.DEPT1 \r\n" +
                    "WHERE A.MTYPE = 'DC' GROUP BY B.DEPT1 ORDER BY B.DEPT1";
                DataSet ds = GetDataSet(sql);
                DataTable dt = ds.Tables[0];
                string sql2 = "SELECT DEPT1,CNAME,EMAIL FROM ICL2_DC_NOTIFY_EMP WHERE DEPT1 like '%0'";
                DataSet ds2 = GetDataSet(sql2);
                DataTable dt2 = ds2.Tables[0];
                string sql3 = "SELECT A.MID as 機台編號, B.DEPT1 as 單位, E.CNAME AS 設備領班名稱, E.EMAIL AS 設備領班信箱, B.cname as 負責人姓名, B.EMAIL as 負責人信箱,\r\n" +
                        "C.CNAME_1 as 主管姓名, D.EMAIL AS 主管信箱 FROM ICL2_MAC A LEFT JOIN ICL2_DC_NOTIFY_EMP B ON A.FID = B.DEPT1 \r\n" +
                        "LEFT JOIN NOTES.SIGN_FLOW C ON C.DEPT1 = B.DEPT1 LEFT JOIN HRIS.V_PERSONNEL1 D ON C.EMPNO_1 = D.EMPNO \r\n " +
                        "LEFT JOIN HRIS.V_PERSONNEL1 E ON E.EMPNO = A.FOREMAN \r\n" +
                        "WHERE A.MTYPE = 'DC' ORDER BY A.MID";
                DataSet ds3 = GetDataSet(sql3);
                DataTable dt3 = ds3.Tables[0];

                for (int k = 0; k < dt.Rows.Count; k++)//先找531>>532>>53..
                {
                    string dept = dt.Rows[k]["單位"].ToString();
                    string message = "";
                    List<string> sendemail = new List<string>();//收信人員
                    List<DataRow> dt4 = new List<DataRow>();//存531.532..目前單位機台資料

                    for (int m = 0; m < dt3.Rows.Count; m++)
                    {
                        if (dt3.Rows[m]["單位"].ToString() == dept)
                        {
                            dt4.Add(dt3.Rows[m]);
                        }
                    }
                    bool error = false;
                    for (int i = 0; i < dt4.Count; i++)// 找出531.532... ICL2_MAC 中 MTYPE='DC' 的dt4機台清單 找出有無異常
                    {
                        string MID = dt4[i]["機台編號"].ToString();
                        Console.WriteLine(MID);
                        var (statsvalues, lastdate) = Get30Data(MID); // return (list, lastdate)
                        if (statsvalues == null || statsvalues.Count == 0)
                        {
                            continue;
                        }
                        double avg = statsvalues.Average(); //第一輪平均值
                        double std1 = Sample(statsvalues); //第一輪標準差
                        List<double> clean = new List<double>(); //移除離群值
                        for (int j = 0; j < statsvalues.Count; j++)
                        {
                            double v = statsvalues[j];
                            if (std1 == 0 || Math.Abs((v - avg) / std1) <= 3)// Math.Abs 絕對值
                            {
                                clean.Add(v);
                            }
                        }
                        if (clean.Count == 0)
                        {
                            continue;
                        }
                        double lastvalue = statsvalues[0];  //有離群值第一筆
                        double avg2 = clean.Average(); //重新計算平均值
                        double std2 = Sample(clean); //重新計算標準差
                        double UCL = avg2 + 3 * std2; //管制上限
                        double LCL = Math.Max(0, avg2 - 3 * std2);  //管制下限  如是負數顯示為0                     

                        Console.WriteLine($"平均值: {avg:F2}"); //F2只取小數點後兩位
                        Console.WriteLine($"標準差: {std1:F2}");
                        Console.WriteLine($"平均值2: {avg2:F2}");
                        Console.WriteLine($"標準差2: {std2:F2}");
                        Console.WriteLine($"上限: {UCL:F2}");
                        Console.WriteLine($"下限: {LCL:F2}");
                        Console.WriteLine($"最後清運量數值:{lastvalue}");
                        Console.WriteLine($"最後記錄日期:{lastdate}");
                        Console.WriteLine("----------------");

                        bool abnormal1 = lastvalue  > UCL; //判斷條件一：超過 UCL 上限
                        bool abnormal2 = IsConsecutiveTrendGetRecent6Values(statsvalues, count: 6);//判斷條件二：連續 6 點趨勢遞增或遞減  /  int count 沒用到所以可以拿掉count: 6
                        bool abnormal3 = GetRecent4Values(statsvalues, count: 4); //判斷條件三：連續 4 點為 0
                        DateTime time = DateTime.Now;
                        bool abnormal4 = lastdate != DateTime.MinValue && (time - lastdate).Days > 14;//判斷條件四：超過14天沒紀錄
                        if (abnormal1 || abnormal2 || abnormal3 || abnormal4)
                        {
                            error = true; //如有異常
                            message += Send(MID, lastvalue, lastdate, UCL, LCL, avg2, std2, abnormal1, abnormal2, abnormal3,abnormal4);
                            string email1 = dt4[i]["負責人信箱"].ToString();
                            string email2 = dt4[i]["設備領班信箱"].ToString();
                            string email3 = dt4[i]["主管信箱"].ToString();
                            if (!string.IsNullOrEmpty(email1) && !sendemail.Contains(email1))//信箱不能是空的 / 去掉重複
                            {
                                sendemail.Add(email1);
                            }
                            if (!string.IsNullOrEmpty(email2) && !sendemail.Contains(email2))
                            {
                                sendemail.Add(email2);
                            }
                            if (!string.IsNullOrEmpty(email3) && !sendemail.Contains(email3))
                            {
                                sendemail.Add(email3);
                            }
                        }
                    }
                    if (error == true)
                    {
                        for (int n = 0; n < dt2.Rows.Count; n++)
                        {
                            string managerdept = dt2.Rows[n]["DEPT1"].ToString().Trim(); //寄信單位550,910 / 530,540,5T0 沒Trim 會錯
                            string manageremail = dt2.Rows[n]["EMAIL"].ToString().Trim();
                            if (!string.IsNullOrEmpty(manageremail) && !sendemail.Contains(manageremail))//確保不是空的，而且目前寄信清單裡還沒有，兩者都成立時，才把主管加進去
                            {
                                if (dept.Substring(0,2) == managerdept.Substring(0,2))// 比單位前2碼
                                {
                                    sendemail.Add(manageremail);
                                }
                                if (managerdept == "550" || managerdept == "910")//固定發550/910
                                { 
                                    sendemail.Add(manageremail); 
                                }
                            }

                        }
                        TEST_CONTENT = $"<div style ='font-family:標楷體,KaiTi,serif;'>" +
                                       message +
                                       $"</div>";

                        MailMessage mail = new MailMessage();
                        mail.From = new MailAddress(SMTP_FROM, SMTP_FROM_NAME);
                        for (int e = 0; e < sendemail.Count; e++)
                        {
                            mail.To.Add(sendemail[e]);
                        }
                        //mail.To.Add(mailto); //測試用                        
                        mail.Body = TEST_CONTENT ;
                        mail.Subject = $"{title} - 單位: {dept}";
                        mail.IsBodyHtml = true;

                        if (!string.IsNullOrEmpty(SMTP_PORT))
                        {
                            using (SmtpClient smtp = new SmtpClient(SMTP_SERV, Convert.ToInt32(SMTP_PORT)))
                            {
                                if (!string.IsNullOrEmpty(SMTP_ACC))
                                    smtp.Credentials = new System.Net.NetworkCredential(SMTP_ACC, SMTP_PWD);
                                smtp.Send(mail);
                            }
                        }
                        else
                        {
                            using (SmtpClient smtp = new SmtpClient(SMTP_SERV))
                            {
                                if (!string.IsNullOrEmpty(SMTP_ACC))
                                    smtp.Credentials = new System.Net.NetworkCredential(SMTP_ACC, SMTP_PWD);
                                smtp.Send(mail);
                            }
                        }

                    }
                    Console.WriteLine("信件寄送成功!", "成功");
                }
            }
            catch (Exception ex)
            {
                using (StreamWriter sw = new StreamWriter("error.txt", true))
                {
                    sw.WriteLine($"{DateTime.Now}{ex.Message}");
                }
            }
        }
        public static DataSet GetDataSet(string sqlStr)
        {
            string connStr = ConfigurationManager.ConnectionStrings["ats"].ConnectionString;
            DataSet ds = new DataSet();
            using (OracleConnection conn = new OracleConnection(connStr))
            {
                conn.Open();
                using (OracleCommand cmd = new OracleCommand(sqlStr, conn))
                {
                    using (OracleDataAdapter da = new OracleDataAdapter(cmd))
                    {
                        da.Fill(ds);
                    }
                }

            }
            return ds;
        }
        public static (List<double> values, DateTime lastdate) Get30Data(string MID)
        {
            List<double> list = new List<double>();
            DateTime lastdate = DateTime.MinValue;
            string connStr = ConfigurationManager.ConnectionStrings["ats"].ConnectionString;
            string sql2 = @"SELECT VAL,IDATE FROM (
                       SELECT CHECKED_VALUE AS VAL,IDATE
                       FROM ICL2_CHECK
                       WHERE IID = '77'
                       AND (MID = :MID OR MID = :MID || '_bak')
                       AND CHECKED_VALUE IS NOT NULL
                       ORDER BY IDATE DESC)
                       WHERE ROWNUM <= 30";
            DataTable dt = new DataTable();
            using (OracleConnection con = new OracleConnection(connStr))
            using (OracleCommand cmd = new OracleCommand(sql2, con))
            {
                cmd.Parameters.Add(new OracleParameter("MID", MID));
                using (OracleDataAdapter d = new OracleDataAdapter(cmd))
                {
                    d.Fill(dt);
                }
            }
            if (dt.Rows.Count == 0) return (list, lastdate);
            lastdate = Convert.ToDateTime(dt.Rows[0]["IDATE"]);
            for (int i = 0; i < dt.Rows.Count; i++)
            {
                object value = dt.Rows[i]["VAL"]; // 1. 不用管它是什麼，先用大盒子（object）把它接住
                if (value != null && value != DBNull.Value)// 2. 安全檢查：確保這個盒子裡「真的有東西」，而且不是資料庫的空值（DBNull）
                {
                    double validDouble = Convert.ToDouble(value);// 3. 安全了！這時候才用 Convert.ToDouble 放心把它轉換成真正的數字
                    list.Add(validDouble);// 4. 放進 double 清單中
                }
            }
            return (list, lastdate);
        }
        public static double Sample(List<double> values)
        {
            if (values == null || values.Count < 2) { return 0; }
            double avg = values.Average();
            double sum = 0;
            for (int i = 0; i < values.Count; i++)
            {
                sum += (values[i] - avg) * (values[i] - avg);
            }
            return Math.Sqrt(sum / (values.Count - 1));

        }
        public static bool IsConsecutiveTrendGetRecent6Values(List<double> ad, int count)  //這樣沒用到 int count 可移除
        {
            if (ad.Count < 6 || ad == null) return false;
            bool isAscending = ad[0] > ad[1] && ad[1] > ad[2] && ad[2] > ad[3] && ad[3] > ad[4] && ad[4] > ad[5];
            bool isDescending = ad[0] < ad[1] && ad[1] < ad[2] && ad[2] < ad[3] && ad[3] < ad[4] && ad[4] < ad[5];
            return isAscending || isDescending;
        }

        public static bool GetRecent4Values(List<double> ad, int count)
        {
            if (ad.Count < 4 || ad == null) return false;
            if (ad[0] == 0 && ad[1] == 0 && ad[2] == 0 && ad[3] == 0)
            {
                return true;
            }
            return false;
        }
        public static string Send(string MID, double lastvalue, DateTime lastdate, double UCL, double LCL, double avg2, double std2,
                                            bool abnormal1, bool abnormal2, bool abnormal3, bool abnormal4)
        {
            List<string> reasons = new List<string>();
            if (abnormal1)
            {
                reasons.Add("最新數值超過管制上限（UCL）");
            }
            if (abnormal2)
            {
                reasons.Add("連續 6 點趨勢遞增或遞減");
            }
            if (abnormal3)
            {
                reasons.Add("連續 4 點清運量為 0");
            }
            if (abnormal4)
            {
                reasons.Add("超過14天無清運量紀錄");
            }
            string newmessage = string.Join(",", reasons);
            string message = $"<p>機台編號:{MID}</p>" +
                $"<p>最後記錄日期:{lastdate:yyyy/MM/dd}</p>" +
                $"<p>清運量數值:{lastvalue}</p>" +
                $"<p>異常原因:{newmessage}</p>" +
                $"<p>------------------------</p>";

            return message;
        }
    }


}


