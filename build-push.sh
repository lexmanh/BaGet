echo "Login to local registry"
docker login cr-app.loctroi.vn -u developer -p Harbor12345

echo "Build and push baget to local registry"
docker buildx build --platform "linux/amd64" \
   -f ./Dockerfile \
   -t cr-app.loctroi.vn/loctroi/baget:latest \
   . --network=host --add-host nuget.loctroi.vn:192.168.231.91 --add-host cr-ap.loctroi.vn:192.168.231.92 --push


