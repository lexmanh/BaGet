echo "Login to local registry"
docker login cr.uef.edu.vn -u developer

echo "Build and push baget to local registry"
docker buildx build --platform "linux/amd64" \
   -f ./Dockerfile \
   -t cr.uef.edu.vn/uef/baget:latest \
   . --push


