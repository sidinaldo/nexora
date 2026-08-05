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
      tipoMidia: 'nenhum', midiaNome: null, midiaMime: null,
      enviadoPor: null, enviadoPorNome: null, deLembrete: false, ...over
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

  beforeEach(() => {
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
});
