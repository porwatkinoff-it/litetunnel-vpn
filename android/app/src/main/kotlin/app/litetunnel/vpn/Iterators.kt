package app.litetunnel.vpn

import io.nekohasekai.libbox.NetworkInterfaceIterator
import io.nekohasekai.libbox.RoutePrefix
import io.nekohasekai.libbox.RoutePrefixIterator
import io.nekohasekai.libbox.StringIterator
import io.nekohasekai.libbox.NetworkInterface as BoxInterface

internal class StringList(private val items: List<String>) : StringIterator {
    private var index = 0
    override fun hasNext() = index < items.size
    override fun len() = items.size
    override fun next() = items[index++]
}

internal class InterfaceList(private val items: List<BoxInterface>) : NetworkInterfaceIterator {
    private var index = 0
    override fun hasNext() = index < items.size
    override fun next() = items[index++]
}

internal fun StringIterator?.toList(): List<String> {
    val iterator = this ?: return emptyList()
    val list = ArrayList<String>()
    while (iterator.hasNext()) list.add(iterator.next())
    return list
}

internal fun RoutePrefixIterator?.toList(): List<RoutePrefix> {
    val iterator = this ?: return emptyList()
    val list = ArrayList<RoutePrefix>()
    while (iterator.hasNext()) list.add(iterator.next())
    return list
}
