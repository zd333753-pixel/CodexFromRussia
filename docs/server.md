# Отдельный SSH-прокси на Linux

Используйте существующий SSH с разрешённым TCP forwarding или создайте отдельную службу. Следующие команды рассчитаны на Ubuntu с systemd. Замените пример публичного ключа своим. Пароли, приватные ключи и личный connection.ini в GitHub не загружайте.

## Отдельный пользователь

```sh
sudo useradd --create-home --shell /usr/sbin/nologin codex-tunnel
sudo passwd -l codex-tunnel
sudo install -d -m 700 -o codex-tunnel -g codex-tunnel /home/codex-tunnel/.ssh
```

Создайте `/home/codex-tunnel/.ssh/authorized_keys` с одной строкой:

```text
restrict,port-forwarding ssh-ed25519 YOUR_PUBLIC_KEY your-comment
```

```sh
sudo chown codex-tunnel:codex-tunnel /home/codex-tunnel/.ssh/authorized_keys
sudo chmod 600 /home/codex-tunnel/.ssh/authorized_keys
```

## Отдельная служба

Проверьте `sudo ss -lntup`: TCP 443 должен быть свободен. Если занят, выберите другой свободный порт и укажите его в connection.ini. Проверьте также firewall сервера и панель хостинга. Не перезаписывайте существующий файл настройки с таким же именем.

Создайте `/etc/ssh/sshd_config_codex_proxy`:

```text
Port 443
ListenAddress 0.0.0.0
HostKey /etc/ssh/ssh_host_ed25519_key
PidFile /run/sshd-codex-proxy.pid
UsePAM yes
PubkeyAuthentication yes
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
AllowUsers codex-tunnel
AllowTcpForwarding local
AllowStreamLocalForwarding no
GatewayPorts no
X11Forwarding no
PermitTTY no
MaxSessions 0
AuthorizedKeysFile .ssh/authorized_keys
```

Создайте `/etc/systemd/system/codex-ssh-proxy.service`:

```ini
[Unit]
Description=Dedicated SSH proxy for Codex
After=network.target
[Service]
ExecStartPre=/usr/sbin/sshd -t -f /etc/ssh/sshd_config_codex_proxy
ExecStart=/usr/sbin/sshd -D -f /etc/ssh/sshd_config_codex_proxy
Restart=on-failure
RestartSec=3
[Install]
WantedBy=multi-user.target
```

```sh
sudo /usr/sbin/sshd -t -f /etc/ssh/sshd_config_codex_proxy
sudo systemctl daemon-reload
sudo systemctl enable --now codex-ssh-proxy.service
sudo systemctl status codex-ssh-proxy.service
```

Эта служба не требует изменения или перезапуска основного SSH, контейнеров Amnezia или их UDP-портов. Смена номера порта не превращает SSH в HTTPS. Сначала убедитесь, что сервер доступен из клиентской сети.

Остановка только прокси:

```sh
sudo systemctl disable --now codex-ssh-proxy.service
```
