# 使用 .NET 8.0 SDK 作为构建镜像
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 复制项目文件
COPY ["DicomSCP.csproj", "./"]
RUN dotnet restore "DicomSCP.csproj"

# 复制所有源代码
COPY . .

# 发布应用
RUN dotnet publish "DicomSCP.csproj" -c Release -o /app/publish -r linux-x64 --self-contained false /p:UseAppHost=false

# 使用运行时镜像
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# 安装 SQLite 和其他依赖
RUN apt-get update \
    && apt-get install -y sqlite3 libsqlite3-dev \
    && rm -rf /var/lib/apt/lists/*

# 复制发布的应用
COPY --from=build /app/publish .

# 设置环境变量
ENV TZ=Asia/Shanghai
# 清空默认端口设置
ENV ASPNETCORE_HTTP_PORTS=""  

# 暴露端口
EXPOSE 5000 11112 11113 11114 11115

# 设置入口点
ENTRYPOINT ["dotnet", "DicomSCP.dll"] 