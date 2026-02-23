# GitLab Runner Setup for Kavita CI/CD

This guide sets up a shell-based GitLab Runner on `git.kw.at` that builds a Docker image and deploys it to `192.168.0.75`.

## Prerequisites

- GitLab CE running on `git.kw.at`
- Docker installed on `git.kw.at`
- Docker installed on `192.168.0.75`
- SSH access from `git.kw.at` to `192.168.0.75`

## 1. Install GitLab Runner

```bash
# On git.kw.at
curl -L https://packages.gitlab.com/install/repositories/runner/gitlab-runner/script.deb.sh | sudo bash
sudo apt-get install gitlab-runner
```

## 2. Create and Register the Runner

The old `--registration-token` flow is deprecated. Create the runner in the GitLab UI instead:

1. Go to **GitLab > Your Project > Settings > CI/CD > Runners**
2. Click **New project runner**
3. Set the tag to `shell`, add a description (e.g. "shell-runner"), and click **Create runner**
4. GitLab will display an authentication token (starts with `glrt-`) — copy it

Then register with the token:

```bash
sudo gitlab-runner register \
  --url https://git.kw.at \
  --token glrt-XXXXX \
  --executor shell \
  --description "shell-runner"
```

## 3. Add gitlab-runner User to Docker Group

```bash
sudo usermod -aG docker gitlab-runner
# Restart the runner to pick up the group change
sudo gitlab-runner restart
```

## 4. Generate SSH Key Pair

```bash
sudo su - gitlab-runner
ssh-keygen -t ed25519 -C "gitlab-ci-deploy" -f ~/.ssh/id_deploy -N ""
cat ~/.ssh/id_deploy.pub
```

Copy the public key output.

## 5. Add Public Key to Deploy Target

```bash
# On 192.168.0.75
ssh root@192.168.0.75
mkdir -p ~/.ssh
# Paste the public key from step 4
echo "PASTE_PUBLIC_KEY_HERE" >> ~/.ssh/authorized_keys
chmod 600 ~/.ssh/authorized_keys
```

## 6. Add Private Key as GitLab CI/CD Variable

1. Go to **GitLab > Your Project > Settings > CI/CD > Variables**
2. Click **Add variable**
3. Key: `SSH_PRIVATE_KEY`
4. Value: Paste the contents of `~gitlab-runner/.ssh/id_deploy`
5. Type: Variable
6. Check: **Mask variable**
7. Optionally check: **Protect variable** (limits to protected branches)

## 7. Test SSH Connectivity

```bash
# As gitlab-runner user on git.kw.at
sudo su - gitlab-runner
ssh -i ~/.ssh/id_deploy -o StrictHostKeyChecking=no root@192.168.0.75 "docker ps"
```

## 8. Verify the Pipeline

1. Push your code (with `Dockerfile.deploy` and `.gitlab-ci.yml`) to GitLab
2. Go to **CI/CD > Pipelines**
3. Click **Run pipeline** (both jobs are manual)
4. Click the play button on the **build** job — wait for the Docker image to build
5. Once build succeeds, click the play button on the **deploy** job
6. Verify: `ssh root@192.168.0.75 "docker ps"` shows kavita running
7. Access `http://192.168.0.75:6000` to confirm the app is working

## Troubleshooting

### Build fails with permission denied
Make sure `gitlab-runner` is in the `docker` group and the runner was restarted.

### Deploy fails with SSH error
Check that `SSH_PRIVATE_KEY` is set correctly in CI/CD variables and the public key is in `authorized_keys` on the target host.

### Container starts but app is not accessible
Check container logs: `ssh root@192.168.0.75 "docker logs kavita"`
