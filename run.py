"""
Stereo Splitter 编译与启动脚本
自动编译 C# 源码并运行，或直接运行已编译的 exe。
"""
import os
import subprocess
import sys

DIR = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(DIR, "StereoSplitter.cs")
EXE = os.path.join(DIR, "StereoSplitter.exe")
CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"


def compile_exe():
    if not os.path.exists(CSC):
        print("错误: 找不到 csc.exe (.NET Framework)")
        sys.exit(1)
    print("正在编译 StereoSplitter.cs ...")
    result = subprocess.run(
        [CSC, "/target:exe", "/out:" + EXE, "/platform:x64", SRC],
        cwd=DIR,
    )
    if result.returncode != 0 or not os.path.exists(EXE):
        print("编译失败!")
        sys.exit(1)
    print("编译成功: " + EXE)


def main():
    # --build 参数强制重新编译
    force_build = "--build" in sys.argv

    if force_build or not os.path.exists(EXE) or (
        os.path.getmtime(SRC) > os.path.getmtime(EXE)
    ):
        compile_exe()

    print("正在启动 StereoSplitter ...")
    sys.exit(subprocess.run([EXE], cwd=DIR).returncode)


if __name__ == "__main__":
    main()
