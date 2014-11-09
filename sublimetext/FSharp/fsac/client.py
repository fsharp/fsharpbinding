import threading
import queue
import json

from .server import requests_queue
from .server import responses_queue

from FSharp.sublime_plugin_lib import PluginLogger


_logger = PluginLogger(__name__)


def read_reqs(responses, req_proc):
    """Reads responses from server and forwards them to @req_proc.
    """
    while True:
        try:
            data = responses.get(block=True, timeout=5)
            if not data:
                print ('no data to consume; exiting')
                break

            try:
                if actions.get(block=False) == STOP_SIGNAL:
                    print ('asked to stop; complying')
                    break
            except:
                pass

            _logger.debug('request data: %s', data)
            req_proc (json.loads(data.decode('utf-8')))
        except queue.Empty:
            pass


class FsacClient(object):
    """Client for fsac server.
    """
    def __init__(self, server, req_proc):
        self.requests = requests_queue
        self.server = server

        threading.Thread(target=read_reqs, args=(responses_queue, req_proc)).start()

    def stop(self):
        self.server.stop()

    def send_request(self, request):
        self.requests.put(request.encode())
