# ControlPhone - cau hinh MikroTik cho farm dien thoai (RB5009UG+S+, RouterOS 7)
#
# So do:
#   Internet - modem nha mang (LAN cua modem phai KHAC 192.168.0.0/23, vd. 192.168.2.1)
#            - ether1 (cong 2.5G "PoE IN")  = WAN, nhan IP tu modem
#            - ether2..ether8 + sfp-sfpplus1 = LAN cua farm (chung 1 mang)
#   LAN farm : 192.168.0.0/23 (192.168.0.1 - 192.168.1.254, 510 dia chi)
#   Router   : 192.168.1.1   (gateway + DNS cho ca farm)
#   PC       : 192.168.1.5   (dat tinh, mat na 255.255.254.0) - dien thoai van goi server o 192.168.1.5 nhu cu
#   Tinh     : 192.168.1.2 - 192.168.1.99 de cho PC, AP, switch (khong cap DHCP)
#   DHCP     : 192.168.0.10-192.168.0.254 + 192.168.1.100-192.168.1.254 (~400 may), giu IP 1 ngay
#
# Cach nap (xoa sach cau hinh cu roi chay file nay, 1 lan):
#   1. WinBox > Files: keo tha file farm-router.rsc vao.
#   2. WinBox > New Terminal, go:
#        /system reset-configuration no-defaults=yes skip-backup=yes run-after-reset=farm-router.rsc
#   3. Router khoi dong lai (~1 phut). WinBox > Neighbors > bam vao dia chi MAC de dang nhap lai
#      (mat khau la mat khau in tren tem duoi day), roi doi mat khau ngay.

:delay 15s

/system identity set name=farm-router
/system clock set time-zone-name=Asia/Ho_Chi_Minh

# LAN: 1 bridge cho moi cong tru ether1
/interface bridge add name=bridge-lan comment="farm LAN"
:foreach i in={"ether2";"ether3";"ether4";"ether5";"ether6";"ether7";"ether8";"sfp-sfpplus1"} do={
    /interface bridge port add bridge=bridge-lan interface=$i
}
/interface list add name=WAN
/interface list add name=LAN
/interface list member add list=WAN interface=ether1
/interface list member add list=LAN interface=bridge-lan

# dia chi
/ip address add address=192.168.1.1/23 interface=bridge-lan comment="farm gateway"
/ip dhcp-client add interface=ether1 disabled=no comment="WAN tu modem"

# DHCP cho farm (CHI 1 noi cap IP trong farm la router nay)
/ip pool add name=farm ranges=192.168.0.10-192.168.0.254,192.168.1.100-192.168.1.254
/ip dhcp-server add name=farm interface=bridge-lan address-pool=farm lease-time=1d disabled=no
/ip dhcp-server network add address=192.168.0.0/23 gateway=192.168.1.1 dns-server=192.168.1.1

# DNS: router tra loi + nho dem cho 200 may (do tai cho modem)
/ip dns set allow-remote-requests=yes servers=8.8.8.8,1.1.1.1

# Internet cho farm
/ip firewall nat add chain=srcnat out-interface-list=WAN action=masquerade comment="farm ra Internet"

# tuong lua: chi quan ly router tu LAN; fasttrack de 200 may chay nhe CPU
/ip firewall filter
add chain=input connection-state=established,related,untracked action=accept
add chain=input connection-state=invalid action=drop
add chain=input protocol=icmp action=accept
add chain=input in-interface-list=LAN action=accept
add chain=input action=drop comment="chan truy cap router tu WAN"
add chain=forward action=fasttrack-connection connection-state=established,related hw-offload=yes
add chain=forward connection-state=established,related,untracked action=accept
add chain=forward connection-state=invalid action=drop
add chain=forward in-interface-list=WAN connection-nat-state=!dstnat action=drop

# chi cho phep tim thay / dang nhap WinBox qua MAC tu phia LAN
/ip neighbor discovery-settings set discover-interface-list=LAN
/tool mac-server set allowed-interface-list=LAN
/tool mac-server mac-winbox set allowed-interface-list=LAN

# gio chuan (log, lich)
/system ntp client set enabled=yes
/system ntp client servers add address=pool.ntp.org

:log info "farm-router.rsc: xong"
