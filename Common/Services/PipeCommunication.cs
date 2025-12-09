using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace InnoPVManagementSystem.Common.Services
{
    public class PipeCommunication : IDisposable
    {
        private const int GET_RETURN_VALUE = 0x70;
        private const int REFRESH_MODEL = 0x71;

        // 개발툴이랑 pipe 통신을 하기 위한 클래스
        [DllImport("kernel32.dll", EntryPoint = "GetStdHandle", SetLastError = true, CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr GetStdHandle(int standardHandle);
        // private const int STANDARD_INPUT_HANDLE = -10;
        private const int STANDARD_OUTPUT_HANDLE = -11;

        // private IntPtr m_ReadHandle = IntPtr.Zero;
        private IntPtr m_WriteHandle = IntPtr.Zero;

        // private PipeStream clientIn;    // ide -> moduleflow
        private PipeStream? clientOut;   // moduleflow -> ide
        
        // 싱글톤 인스턴스
        private static PipeCommunication? _instance;
        private static readonly object _lock = new object();
        private bool _disposed = false;


        private PipeCommunication()  
        {
            try
            {
                // 보내는 핸들러 생성, 받는 쪽은 만들지 않음
                m_WriteHandle = GetStdHandle(STANDARD_OUTPUT_HANDLE);
                
                // 핸들러 유효성 검증
                if (m_WriteHandle == IntPtr.Zero || m_WriteHandle == new IntPtr(-1))
                {
                    throw new InvalidOperationException($"표준 출력 핸들러를 가져올 수 없습니다. 핸들러 값: {m_WriteHandle}");
                }
                
                // 파이프 스트림 생성
                clientOut = new AnonymousPipeClientStream(PipeDirection.Out, m_WriteHandle.ToString());
                
                // 파이프 스트림 유효성 검증
                if (clientOut == null)
                {
                    throw new InvalidOperationException("AnonymousPipeClientStream을 생성할 수 없습니다.");
                }
                
                // 연결 상태 확인
                if (!clientOut.IsConnected)
                {
                    throw new InvalidOperationException("파이프 스트림이 연결되지 않았습니다.");
                }
                
                System.Diagnostics.Debug.WriteLine($"PipeCommunication 초기화 성공 - 핸들러: {m_WriteHandle}, 연결됨: {clientOut.IsConnected}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"PipeCommunication 초기화 실패: {ex.Message}");
                throw new InvalidOperationException($"파이프 통신 초기화에 실패했습니다: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// 싱글톤 인스턴스를 반환합니다.
        /// </summary>
        public static PipeCommunication Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new PipeCommunication();
                        }
                    }
                }
                return _instance;
            }
        }
        
        /// <summary>
        /// 파이프 통신을 초기화하고 ReleaseBlocking을 실행합니다.
        /// 프로그램 시작 시 한 번만 호출해야 합니다.
        /// </summary>
        public static void InitializeAndReleaseBlocking()
        {
            try
            {
                var instance = Instance;
                instance.ReleaseBlocking();
                System.Diagnostics.Debug.WriteLine("파이프 통신 초기화 및 ReleaseBlocking 완료");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"파이프 통신 초기화 실패: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// 싱글톤 인스턴스를 해제합니다.
        /// 프로그램 종료 시 호출해야 합니다.
        /// </summary>
        public static void CloseInstance()
        {
            lock (_lock)
            {
                if (_instance != null)
                {
                    try
                    {
                        _instance.Close();
                        System.Diagnostics.Debug.WriteLine("파이프 통신 인스턴스 해제 완료");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"파이프 통신 인스턴스 해제 중 오류: {ex.Message}");
                    }
                    finally
                    {
                        _instance = null;
                    }
                }
            }
        }

        // 개발툴의 blocking 해제
        public void ReleaseBlocking()
        {
            if (clientOut != null)
            {
                clientOut.Write(Encoding.UTF8.GetBytes("r/1/1"));
                clientOut.Flush();
            }
        }

        // 특정 모델에 대해 리프레시 하도록 파이프 요청하는 함수 (0x32로 호출)
        public void RefreshModel(string modelName)
        {
            try
            {
                Thread.Sleep(300);  // 완료 속도 지연

                if (string.IsNullOrEmpty(modelName))
                    return;

                if (clientOut != null)
                {
                    string send_msg = "p/1/1/" + modelName;

                    byte[] data = Encoding.UTF8.GetBytes(send_msg);
                    clientOut.Write(data);
                    clientOut.Flush();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RefreshModel 오류: {ex.Message}");
                MessageBox.Show("[1] Reponse Error.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 파이프 통신 상태를 확인합니다.
        /// </summary>
        /// <returns>파이프 통신이 정상적으로 작동하는지 여부</returns>
        public bool IsValid()
        {
            try
            {
                // 이미 해제된 경우
                if (_disposed)
                {
                    return false;
                }
                
                // 핸들러 유효성 확인
                if (m_WriteHandle == IntPtr.Zero || m_WriteHandle == new IntPtr(-1))
                {
                    return false;
                }
                
                // 파이프 스트림 유효성 확인
                if (clientOut == null)
                {
                    return false;
                }
                
                // 연결 상태 확인
                return clientOut.IsConnected;
            }
            catch
            {
                return false;
            }
        }
        
        public void Close()
        {
            Dispose();
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try
                    {
                        if (clientOut != null)
                        {
                            // 파이프 스트림이 연결되어 있으면 정리
                            if (clientOut.IsConnected)
                            {
                                clientOut.Flush(); // 남은 데이터 전송
                            }
                            
                            clientOut.Dispose();
                            clientOut = null;
                            System.Diagnostics.Debug.WriteLine("파이프 스트림 해제 완료");
                        }
                        
                        // 핸들러 초기화
                        m_WriteHandle = IntPtr.Zero;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"파이프 해제 중 오류: {ex.Message}");
                    }
                }
                
                _disposed = true;
            }
        }
        
        ~PipeCommunication()
        {
            Dispose(false);
        }
    }
}
