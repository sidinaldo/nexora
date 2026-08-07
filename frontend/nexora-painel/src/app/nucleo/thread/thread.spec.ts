import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of } from 'rxjs';
import { CaixaServico } from '../servicos/caixa.servico';
import { RealtimeServico } from '../servicos/realtime.servico';
import { ToastServico } from '../toast/toast.servico';
import { MensagemDto, PaginaCursor, RespostaEnviada } from '../modelos';
import { Thread } from './thread';

/** A THREAD é o componente com mais mecânica escondida do painel, e é compartilhado entre a
 *  caixa de entrada e o detalhe do contato. Quebra aqui é sutil por natureza: a rolagem pula,
 *  a mensagem nova rouba a leitura, o cursor repete página. Nada disso lança erro.
 *
 *  Testar isso é o que evita "consertar duas vezes" — que era o motivo de o componente existir. */
describe('Thread', () => {
  let caixa: CaixaFalso;
  let realtime: RealtimeFalso;
  let toast: ToastFalso;
  let fixture: ComponentFixture<Thread>;
  let componente: Thread;

  function msg(id: number, over: Partial<MensagemDto> = {}): MensagemDto {
    return {
      id, direcao: 'entrada', texto: `mensagem ${id}`, ack: null,
      enviadaEm: null, recebidaEm: '2026-08-06T10:00:00', expiradaEm: null, erro: null,
      tipoMidia: 'nenhum', midiaNome: null, midiaMime: null, midiaBytes: null, midiaDuracaoSegundos: null,
      enviadoPor: null, enviadoPorNome: null, deLembrete: false, recuperadaEm: null, ...over
    };
  }

  class CaixaFalso {
    chamadas: { conversaId: number; antes?: number; tamanho?: number }[] = [];
    respondeu: { conversaId: number; texto: string }[] = [];
    lidas: number[] = [];
    pagina: PaginaCursor<MensagemDto> = { itens: [], temMais: false };
    resposta: RespostaEnviada = { mensagemId: 99, enviada: true, erro: null };

    mensagens(conversaId: number, antes?: number, tamanho?: number)
      : Observable<PaginaCursor<MensagemDto>> {
      this.chamadas.push({ conversaId, antes, tamanho });
      return of(this.pagina);
    }
    responder(conversaId: number, texto: string): Observable<RespostaEnviada> {
      this.respondeu.push({ conversaId, texto });
      return of(this.resposta);
    }
    marcarLida(conversaId: number): Observable<void> {
      this.lidas.push(conversaId);
      return of(undefined);
    }

    // ---- MID-1 ----
    /** Blobs por mensagem. `null` = a busca falha, para exercitar o marcador de erro. */
    blobs: Record<number, Blob | null> = {};
    reenviados: number[] = [];

    midia(mensagemId: number): Observable<Blob> {
      const b = this.blobs[mensagemId];
      return b === null
        ? new Observable<Blob>(s => s.error(new Error('404')))
        : of(b ?? new Blob(['x'], { type: 'image/png' }));
    }
    reenviar(mensagemId: number): Observable<RespostaEnviada> {
      this.reenviados.push(mensagemId);
      return of(this.resposta);
    }

    audiosEnviados: { conversaId: number; tipo: string }[] = [];
    enviarAudio(conversaId: number, audio: Blob): Observable<RespostaEnviada> {
      this.audiosEnviados.push({ conversaId, tipo: audio.type });
      return of(this.resposta);
    }
  }

  class RealtimeFalso {
    mensagemRecebida$ = new Subject<{ conversaId: number }>();
    statusMensagem$ = new Subject<unknown>();
  }

  class ToastFalso {
    erros: string[] = [];
    erro(m: string) { this.erros.push(m); }
  }

  /** `aposRender` usa setTimeout(0). Sem zone.js no modo zoneless, esperar um macrotask real é
   *  mais honesto (e mais estável) do que fingir o relógio. */
  const aposORender = () => new Promise<void>(r => setTimeout(r, 0));

  async function montar(conversaId = 1, naoLidas = 0) {
    fixture = TestBed.createComponent(Thread);
    componente = fixture.componentInstance;
    fixture.componentRef.setInput('conversaId', conversaId);
    fixture.componentRef.setInput('naoLidas', naoLidas);
    fixture.detectChanges();          // dispara o effect que chama abrir()
    await aposORender();
    fixture.detectChanges();
  }

  /** Num runner headless nada é layoutado: `scrollHeight` é sempre 0 e `scrollTop` é grampeado
   *  em 0, então a âncora não teria como ser exercitada.
   *
   *  As métricas são sobrescritas NO ELEMENTO REAL, e não trocando o `@ViewChild` por um objeto
   *  falso, porque qualquer detecção de mudanças re-consulta a ViewChild e devolveria o elemento
   *  original — o teste da âncora passava a medir um objeto que o componente já tinha largado. */
  function comElemento(metricas: { scrollTop: number; scrollHeight: number; clientHeight: number }) {
    const el = fixture.nativeElement.querySelector('.thread') as HTMLDivElement;
    const estado = { ...metricas };

    Object.defineProperty(el, 'scrollTop', {
      configurable: true,
      get: () => estado.scrollTop,
      set: (v: number) => { estado.scrollTop = v; }
    });
    Object.defineProperty(el, 'scrollHeight', { configurable: true, get: () => estado.scrollHeight });
    Object.defineProperty(el, 'clientHeight', { configurable: true, get: () => estado.clientHeight });
    el.scrollTo = ((o: { top: number }) => { estado.scrollTop = o.top; }) as typeof el.scrollTo;

    return estado;
  }

  /** ===================== POR QUE UM IntersectionObserver FALSO =====================
   *  O de verdade só dispara quando há LAYOUT, e num runner headless nada é layoutado: os balões
   *  têm altura zero e a callback nunca roda. Os testes passariam a medir "o observador não fez
   *  nada" em vez de "o anexo carregou".
   *
   *  Este falso reporta interseção na hora, o que exercita o MESMO ramo do componente — o de
   *  produção, não o de segurança.
   *
   *  Fica no nível de CIMA porque qualquer teste com mídia depende dele: preso a um `describe`,
   *  o `describe` seguinte volta a usar o de verdade e falha por um motivo que não é o dele. */
  let observadorOriginal: typeof IntersectionObserver;

  beforeEach(() => {
    observadorOriginal = window.IntersectionObserver;
    window.IntersectionObserver = class {
      constructor(private cb: IntersectionObserverCallback) { }
      observe(el: Element) {
        this.cb([{ target: el, isIntersecting: true } as IntersectionObserverEntry],
                this as unknown as IntersectionObserver);
      }
      unobserve() { }
      disconnect() { }
      takeRecords() { return []; }
      root = null; rootMargin = ''; thresholds = [];
    } as unknown as typeof IntersectionObserver;

    caixa = new CaixaFalso();
    realtime = new RealtimeFalso();
    toast = new ToastFalso();

    TestBed.configureTestingModule({
      imports: [Thread],
      providers: [
        provideZonelessChangeDetection(),
        { provide: CaixaServico, useValue: caixa },
        { provide: RealtimeServico, useValue: realtime },
        { provide: ToastServico, useValue: toast }
      ]
    });
  });

  afterEach(() => { window.IntersectionObserver = observadorOriginal; });

  describe('abrir a conversa', () => {
    it('carrega as mensagens e sai do estado de carregando', async () => {
      caixa.pagina = { itens: [msg(1), msg(2)], temMais: false };
      await montar(7);

      expect(caixa.chamadas[0].conversaId).toBe(7);
      expect(caixa.chamadas[0].antes).withContext('a 1ª página não tem cursor').toBeUndefined();
      expect(componente.mensagens().length).toBe(2);
      expect(componente.carregando()).toBeFalse();
    });

    it('marca como lida só quando havia não lidas', async () => {
      await montar(7, 0);
      expect(caixa.lidas).toEqual([]);

      caixa.lidas = [];
      await montar(9, 3);
      expect(caixa.lidas).toEqual([9]);
    });

    it('um erro ao carregar não deixa a tela presa em "Carregando…"', async () => {
      caixa.mensagens = () => new Observable(s => s.error(new Error('rede')));
      await montar();
      expect(componente.carregando()).toBeFalse();
    });
  });

  describe('cursor das anteriores', () => {
    it('PEDE A PARTIR DA MAIS ANTIGA e prepende preservando a ordem', async () => {
      // ===================== O ERRO QUE ISTO PEGA =====================
      // O cursor tem que ser o id da mensagem do TOPO. Usando o do fim, a mesma página volta
      // para sempre e o botão "carregar anteriores" nunca anda — sem erro nenhum na tela.
      // ===============================================================
      caixa.pagina = { itens: [msg(10), msg(11), msg(12)], temMais: true };
      await montar(3);

      caixa.pagina = { itens: [msg(7), msg(8), msg(9)], temMais: false };
      componente.carregarAntigas();
      await aposORender();

      const pedido = caixa.chamadas[caixa.chamadas.length - 1];
      expect(pedido.antes).withContext('cursor = id da mensagem mais antiga em tela').toBe(10);

      expect(componente.mensagens().map(m => m.id)).toEqual([7, 8, 9, 10, 11, 12]);
      expect(componente.temMaisAntigas()).toBeFalse();
      expect(componente.carregandoAntigas()).toBeFalse();
    });

    it('ANCORA A ROLAGEM: compensa a altura inserida no topo', async () => {
      // Sem a compensação, a thread pula na cara de quem está lendo.
      caixa.pagina = { itens: [msg(10)], temMais: true };
      await montar(3);

      const el = comElemento({ scrollTop: 200, scrollHeight: 1000, clientHeight: 400 });

      caixa.pagina = { itens: [msg(8), msg(9)], temMais: false };
      componente.carregarAntigas();
      // O componente relê scrollHeight DEPOIS do render; simula o crescimento de 600px.
      el.scrollHeight = 1600;
      await aposORender();

      // 200 + (1600 - 1000) = 800: a mesma mensagem continua sob os olhos do vendedor.
      expect(el.scrollTop).toBe(800);
    });

    it('não pede nada com a thread vazia nem durante um carregamento em curso', async () => {
      caixa.pagina = { itens: [], temMais: true };
      await montar(3);

      const antes = caixa.chamadas.length;
      componente.carregarAntigas();
      expect(caixa.chamadas.length).withContext('sem 1ª mensagem, não há cursor').toBe(antes);

      caixa.pagina = { itens: [msg(5)], temMais: true };
      await montar(3);
      componente.carregandoAntigas.set(true);
      const antes2 = caixa.chamadas.length;
      componente.carregarAntigas();
      expect(caixa.chamadas.length).withContext('não duplica requisição').toBe(antes2);
    });
  });

  describe('mensagem chegando pelo realtime', () => {
    it('NÃO ROUBA A ROLAGEM de quem subiu para ler: mostra o chip', async () => {
      // ===================== POR QUE ISTO IMPORTA =====================
      // Rolar à força enquanto o vendedor lê uma mensagem antiga é a diferença entre uma
      // ferramenta e um estorvo. O chip avisa sem interromper.
      // ===============================================================
      caixa.pagina = { itens: [msg(1)], temMais: false };
      await montar(4);

      // Longe do fim: 2000 - 100 - 400 = 1500px > margem de 150.
      comElemento({ scrollTop: 100, scrollHeight: 2000, clientHeight: 400 });

      realtime.mensagemRecebida$.next({ conversaId: 4 });
      await aposORender();

      expect(componente.temNovaMensagem()).withContext('avisa em vez de rolar').toBeTrue();
    });

    it('rola sozinho para quem já estava no fim', async () => {
      caixa.pagina = { itens: [msg(1)], temMais: false };
      await montar(4);

      // No fim: 1000 - 600 - 400 = 0 <= 150.
      const el = comElemento({ scrollTop: 600, scrollHeight: 1000, clientHeight: 400 });

      realtime.mensagemRecebida$.next({ conversaId: 4 });
      await aposORender();

      expect(componente.temNovaMensagem()).toBeFalse();
      expect(el.scrollTop).toBe(1000);
    });

    it('ignora mensagem de OUTRA conversa', async () => {
      caixa.pagina = { itens: [msg(1)], temMais: false };
      await montar(4);
      const antes = caixa.chamadas.length;

      realtime.mensagemRecebida$.next({ conversaId: 999 });
      await aposORender();

      expect(caixa.chamadas.length).toBe(antes);
      expect(componente.temNovaMensagem()).toBeFalse();
    });

    it('ACK não mexe na posição de leitura', async () => {
      // O tick muda; a rolagem não pode se mover por causa disso.
      caixa.pagina = { itens: [msg(1)], temMais: false };
      await montar(4);

      const el = comElemento({ scrollTop: 100, scrollHeight: 2000, clientHeight: 400 });

      realtime.statusMensagem$.next({});
      await aposORender();

      expect(el.scrollTop).withContext('modo preservar').toBe(100);
      expect(componente.temNovaMensagem()).withContext('ACK não é mensagem nova').toBeFalse();
    });

    it('pede pelo menos uma mensagem a mais do que já tem, sem descartar o topo', async () => {
      caixa.pagina = { itens: Array.from({ length: 50 }, (_, i) => msg(i + 1)), temMais: false };
      await montar(4);

      realtime.mensagemRecebida$.next({ conversaId: 4 });
      await aposORender();

      expect(caixa.chamadas[caixa.chamadas.length - 1].tamanho).toBe(51);
    });
  });

  describe('enviar', () => {
    it('recusa texto vazio ou só espaços', async () => {
      await montar();
      componente.texto.set('   ');
      componente.enviar();
      expect(caixa.respondeu).toEqual([]);
    });

    it('envia, limpa o campo e avisa a tela de fora', async () => {
      await montar(5);
      const mudou: number[] = [];
      componente.mudou.subscribe(() => mudou.push(1));

      componente.texto.set('  bom dia  ');
      componente.enviar();
      await aposORender();

      expect(caixa.respondeu).toEqual([{ conversaId: 5, texto: 'bom dia' }]);
      expect(componente.texto()).toBe('');
      expect(componente.enviando()).toBeFalse();
      expect(mudou.length).toBeGreaterThan(0);
    });

    it('mensagem registrada MAS não entregue avisa sem bloquear', async () => {
      // Não é erro de requisição: a mensagem existe e aparece na thread. O vendedor precisa
      // saber que não chegou, mas a tela não pode travar por isso.
      caixa.resposta = { mensagemId: 1, enviada: false, erro: 'WhatsApp desconectado.' };
      await montar(5);

      componente.texto.set('oi');
      componente.enviar();
      await aposORender();

      expect(toast.erros).toEqual(['WhatsApp desconectado.']);
      expect(componente.enviando()).toBeFalse();
    });

    it('erro de requisição destrava o botão', async () => {
      await montar(5);
      caixa.responder = () => new Observable(s => s.error({ error: { erro: 'Sem permissão.' } }));

      componente.texto.set('oi');
      componente.enviar();

      expect(componente.enviando()).withContext('não pode ficar preso em "Enviando…"').toBeFalse();
      expect(toast.erros).toEqual(['Sem permissão.']);
    });
  });

  it('trocar de conversa recarrega do zero', async () => {
    caixa.pagina = { itens: [msg(1), msg(2)], temMais: false };
    await montar(1);

    caixa.pagina = { itens: [msg(80)], temMais: false };
    fixture.componentRef.setInput('conversaId', 2);
    fixture.detectChanges();
    await aposORender();

    expect(caixa.chamadas[caixa.chamadas.length - 1].conversaId).toBe(2);
    expect(componente.mensagens().map(m => m.id)).toEqual([80]);
    expect(componente.texto()).withContext('rascunho não vaza entre conversas').toBe('');
  });

  // ============================================================ MID-1 · mídia
  describe('mídia na thread', () => {

    /** Sem `IntersectionObserver` (é o caso do jsdom/karma headless aqui), o componente cai no
     *  ramo que carrega tudo — o teste exercita justamente esse caminho de segurança. */
    it('IMAGEM vira miniatura, e o blob é buscado pela rota AUTENTICADA', async () => {
      caixa.pagina = {
        itens: [msg(1, { tipoMidia: 'imagem', midiaNome: 'foto.jpg', midiaBytes: 2048 })],
        temMais: false
      };
      await montar(1);
      await aposORender();
      fixture.detectChanges();

      const img = fixture.nativeElement.querySelector('.midia-imagem img') as HTMLImageElement;
      expect(img).withContext('a imagem aparece como imagem, não como "📎 arquivo"').toBeTruthy();
      // `blob:` prova que passou pelo HttpClient (com Bearer), não por um `src` para a API.
      expect(img.getAttribute('src')!.startsWith('blob:')).toBeTrue();
    });

    it('DOCUMENTO mostra nome e tamanho, com ação de baixar', async () => {
      caixa.pagina = {
        itens: [msg(2, { tipoMidia: 'documento', midiaNome: 'proposta.pdf', midiaBytes: 348_160 })],
        temMais: false
      };
      await montar(1);
      await aposORender();
      fixture.detectChanges();

      const el = fixture.nativeElement.querySelector('.midia-arquivo') as HTMLElement;
      expect(el.textContent).toContain('proposta.pdf');
      expect(el.textContent).toContain('340 KB');
      expect(el.textContent).toContain('baixar');
    });

    it('A LEGENDA aparece junto do anexo', async () => {
      caixa.pagina = {
        itens: [msg(3, { tipoMidia: 'imagem', midiaNome: 'f.jpg', texto: 'segue o orçamento' })],
        temMais: false
      };
      await montar(1);
      await aposORender();
      fixture.detectChanges();

      const balao = fixture.nativeElement.querySelector('.balao') as HTMLElement;
      expect(balao.querySelector('.midia')).toBeTruthy();
      expect(balao.querySelector('.texto')!.textContent).toContain('segue o orçamento');
    });

    it('MÍDIA QUE FALHA NÃO SOME: vira marcador com tentar de novo', async () => {
      // Silêncio faria a mensagem parecer que nunca existiu — e o vendedor não teria o que fazer.
      caixa.blobs[4] = null;
      caixa.pagina = {
        itens: [msg(4, { tipoMidia: 'documento', midiaNome: 'sumiu.pdf' })],
        temMais: false
      };
      await montar(1);
      await aposORender();
      fixture.detectChanges();

      const falhou = fixture.nativeElement.querySelector('.midia-falhou') as HTMLElement;
      expect(falhou).toBeTruthy();
      expect(falhou.textContent).toContain('sumiu.pdf');
      expect(falhou.textContent).toContain('tentar de novo');
    });

    it('anexo FORA da whitelist ou acima do teto é recusado sem subir nada', async () => {
      await montar(1);

      // Conveniência do cliente: o que VALE é a checagem do servidor, que olha os bytes.
      componente['aceitar'](new File(['x'], 'a.zip', { type: 'application/zip' }));
      expect(componente.anexo()).toBeNull();
      expect(componente.erroAnexo()).toContain('imagem');

      const gigante = new File([new Uint8Array(17 * 1024 * 1024)], 'g.jpg', { type: 'image/jpeg' });
      componente['aceitar'](gigante);
      expect(componente.anexo()).toBeNull();
      expect(componente.erroAnexo()).toContain('16 MB');
    });

    it('TENTAR DE NOVO reaproveita a mensagem, não cria outra', async () => {
      caixa.pagina = {
        itens: [msg(9, { direcao: 'saida', erro: 'Evolution fora do ar', enviadaEm: null })],
        temMais: false
      };
      await montar(1);
      await aposORender();
      fixture.detectChanges();

      const botao = [...fixture.nativeElement.querySelectorAll('.nao-entregue button')]
        .find(b => (b as HTMLElement).textContent!.includes('Tentar de novo')) as HTMLButtonElement;
      expect(botao).toBeTruthy();

      botao.click();
      await aposORender();

      expect(caixa.reenviados).toEqual([9]);          // o MESMO id
      expect(caixa.respondeu.length).toBe(0);         // e nenhuma mensagem nova
    });
  });


  // ============================================================ bloco 13 · áudio
  describe('gravação de áudio', () => {
    let mediaOriginal: MediaDevices;
    let recorderOriginal: typeof MediaRecorder;

    /** Um `MediaRecorder` falso, porque o de verdade precisa de microfone. Ele emite um pedaço
     *  de dado e chama `onstop` quando mandam parar — o mínimo para exercitar o fluxo. */
    class RecorderFalso {
      static suportados: string[] = ['audio/webm;codecs=opus'];
      static isTypeSupported(t: string) { return RecorderFalso.suportados.includes(t); }
      ondataavailable: ((e: { data: Blob }) => void) | null = null;
      onstop: (() => void) | null = null;
      constructor(_s: MediaStream, public opcoes: { mimeType: string }) { }
      start() { this.ondataavailable?.({ data: new Blob(['abc'], { type: this.opcoes.mimeType }) }); }
      stop() { this.onstop?.(); }
    }

    let trilhasParadas = 0;

    function permitirMicrofone(ok: boolean) {
      trilhasParadas = 0;
      Object.defineProperty(navigator, 'mediaDevices', {
        configurable: true,
        value: {
          getUserMedia: () => ok
            ? Promise.resolve({ getTracks: () => [{ stop: () => trilhasParadas++ }] } as unknown as MediaStream)
            : Promise.reject(new DOMException('NotAllowedError'))
        }
      });
    }

    beforeEach(() => {
      mediaOriginal = navigator.mediaDevices;
      recorderOriginal = window.MediaRecorder;
      RecorderFalso.suportados = ['audio/webm;codecs=opus'];
      window.MediaRecorder = RecorderFalso as unknown as typeof MediaRecorder;
    });

    afterEach(() => {
      window.MediaRecorder = recorderOriginal;
      Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: mediaOriginal });
    });

    it('PERMISSAO NEGADA mostra mensagem clara, nao falha em silencio', async () => {
      // Sem isto, clicar no microfone não faz nada e o vendedor conclui que o botão quebrou.
      permitirMicrofone(false);
      await montar(1);

      await componente.iniciarGravacao();

      expect(componente.gravando()).toBeFalse();
      expect(componente.erroAnexo()).toContain('microfone');
      expect(componente.erroAnexo()).toContain('Autorize');
    });

    it('NAVEGADOR SEM FORMATO COMPATIVEL avisa o que fazer, e nao grava', async () => {
      // O caso do Safari/iOS: só grava MP4/AAC, que o WhatsApp entrega como arquivo anexo.
      RecorderFalso.suportados = [];
      permitirMicrofone(true);
      await montar(1);

      await componente.iniciarGravacao();

      expect(componente.gravando()).toBeFalse();
      expect(componente.erroAnexo()).toContain('Chrome');
    });

    it('ESCOLHE O MELHOR FORMATO disponivel — OGG antes de WEBM', async () => {
      // OGG dispensa reempacotamento no servidor. Pedir WebM tendo OGG seria trabalho à toa.
      RecorderFalso.suportados = ['audio/ogg;codecs=opus', 'audio/webm;codecs=opus'];
      permitirMicrofone(true);
      await montar(1);

      await componente.iniciarGravacao();
      componente.segundosGravados.set(3);   // abaixo de 1s a prévia é descartada de propósito
      componente.pararGravacao();

      expect(componente.audioGravado()!.blob.type).toBe('audio/ogg;codecs=opus');
    });

    it('gravar, parar e enviar SOLTA O MICROFONE e manda o blob', async () => {
      permitirMicrofone(true);
      await montar(4);

      await componente.iniciarGravacao();
      expect(componente.gravando()).toBeTrue();

      componente.segundosGravados.set(7);
      componente.pararGravacao();

      expect(componente.gravando()).toBeFalse();
      // Microfone solto: senão o indicador do navegador fica aceso e a pessoa se sente ouvida.
      expect(trilhasParadas).toBe(1);

      const previa = componente.audioGravado();
      expect(previa).toBeTruthy();
      expect(previa!.segundos).toBe(7);

      componente.enviarAudio();
      await aposORender();

      expect(caixa.audiosEnviados.length).toBe(1);
      expect(caixa.audiosEnviados[0].conversaId).toBe(4);
      expect(componente.audioGravado()).withContext('a prévia sai depois de enviar').toBeNull();
    });

    it('DESCARTAR durante a gravacao nao deixa previa', async () => {
      permitirMicrofone(true);
      await montar(1);

      await componente.iniciarGravacao();
      componente.segundosGravados.set(5);
      componente.cancelarGravacao();

      expect(componente.gravando()).toBeFalse();
      expect(componente.audioGravado()).toBeNull();
      expect(caixa.audiosEnviados.length).toBe(0);
    });

    it('gravacao de menos de 1 segundo nao vira previa', async () => {
      // Toque acidental no microfone não pode virar uma nota de voz de 0 segundo no cliente.
      permitirMicrofone(true);
      await montar(1);

      await componente.iniciarGravacao();
      componente.pararGravacao();      // segundosGravados continua 0

      expect(componente.audioGravado()).toBeNull();
    });

    it('o player mostra a duracao que veio do BANCO', async () => {
      // O `<audio>` só sabe a duração depois de carregar os metadados; até lá mostraria 0:00.
      caixa.pagina = {
        itens: [msg(20, { tipoMidia: 'audio', midiaNome: 'voz.ogg', midiaDuracaoSegundos: 67 })],
        temMais: false
      };
      await montar(1);
      await aposORender();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.midia-audio audio')).toBeTruthy();
      expect(fixture.nativeElement.querySelector('.audio-duracao')!.textContent).toContain('1:07');
    });
  });


  describe('altura do compositor', () => {
    /** O `textarea` global tem `min-height: 90px` — regra de formulário. Num compositor de chat
     *  ela reserva cinco linhas para "ok" e come o espaço da conversa. */
    it('NASCE COM UMA LINHA, nao com as cinco do estilo global', async () => {
      await montar(1);
      const campo = fixture.nativeElement.querySelector('.linha-compositor textarea') as HTMLTextAreaElement;

      expect(campo.rows).toBe(1);
      expect(getComputedStyle(campo).resize)
        .withContext('a alça de redimensionar sai: o campo cresce sozinho').toBe('none');
      expect(parseInt(getComputedStyle(campo).maxHeight)).toBeLessThanOrEqual(140);
    });

    it('CRESCE com o texto e VOLTA ao encolher', async () => {
      // `height = auto` antes de ler `scrollHeight` é o que permite VOLTAR. Sem isso o campo só
      // sabe crescer, e apagar três linhas o deixa alto e vazio para sempre.
      await montar(1);
      const campo = fixture.nativeElement.querySelector('.linha-compositor textarea') as HTMLTextAreaElement;

      // `scrollHeight` num runner headless é sempre 0; o teste controla o valor para medir a
      // MECÂNICA (a ordem das atribuições), que é o que pode quebrar.
      let simulado = 96;
      Object.defineProperty(campo, 'scrollHeight', { configurable: true, get: () => simulado });

      componente.ajustarAltura(campo);
      expect(campo.style.height).toBe('96px');

      simulado = 40;
      componente.ajustarAltura(campo);
      expect(campo.style.height).withContext('encolheu de volta').toBe('40px');
    });

    it('enviar devolve o campo para uma linha', async () => {
      await montar(1);
      const campo = fixture.nativeElement.querySelector('.linha-compositor textarea') as HTMLTextAreaElement;
      campo.style.height = '120px';

      componente.texto.set('oi');
      componente.enviar();
      await aposORender();

      expect(campo.style.height).withContext('campo vazio e alto seria um buraco na tela').toBe('');
    });
  });

});
