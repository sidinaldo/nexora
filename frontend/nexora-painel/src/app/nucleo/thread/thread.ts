import {
  Component, ElementRef, OnDestroy, ViewChild, effect, inject, input, output, signal, untracked
} from '@angular/core';
import { HttpEventType } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { Subscription } from 'rxjs';
import { CaixaServico } from '../servicos/caixa.servico';
import { RealtimeServico } from '../servicos/realtime.servico';
import { ToastServico } from '../toast/toast.servico';
import { MensagemDto } from '../modelos';
import { TickStatus, estadoDoAck, rotuloAck } from '../tick-status/tick-status';

/** A THREAD DA CONVERSA — mensagens, rolagem e envio.
 *
 *  ===================== POR QUE ISTO É UM COMPONENTE =====================
 *  Duas telas mostram a mesma conversa: a caixa de entrada e o detalhe do contato. A mecânica
 *  aqui NÃO é trivial e é justamente a que se erra ao duplicar:
 *
 *  - paginação por CURSOR para trás (as mais novas antes de um id), nunca por offset;
 *  - ÂNCORA DE ROLAGEM ao carregar as anteriores: compensa o scrollTop pela altura inserida no
 *    topo, senão a thread "pula" na cara de quem está lendo;
 *  - rolagem 'auto' quando chega mensagem: rola só se o vendedor JÁ estava no fim; se ele
 *    subiu para ler algo, mostra o chip "nova mensagem" e NÃO rouba a rolagem.
 *
 *  Duplicar isso significa consertar cada bug duas vezes — e descobrir o segundo bug meses
 *  depois, na tela que ninguém testou.
 *  ========================================================================
 *
 *  O CABEÇALHO fica fora: a caixa mostra assumir/liberar, o detalhe do contato mostra outra
 *  coisa. O que é comum é a thread, não o que está em volta dela. */
@Component({
  selector: 'app-thread',
  imports: [FormsModule, DatePipe, TickStatus],
  templateUrl: './thread.html',
  styleUrl: './thread.css'
})
export class Thread implements OnDestroy {
  private servico = inject(CaixaServico);
  private realtime = inject(RealtimeServico);
  private toast = inject(ToastServico);

  /** A conversa a mostrar. Trocar o valor recarrega tudo. */
  conversaId = input.required<number>();

  /** Quantas não lidas a conversa tinha ao abrir — dispara o "marcar lida". */
  naoLidas = input(0);

  /** Algo mudou que a tela de fora precisa saber: mensagem enviada ou conversa marcada como
   *  lida. A lista da caixa se reordena com isso; o detalhe do contato recarrega o cabeçalho. */
  mudou = output<void>();

  mensagens = signal<MensagemDto[]>([]);
  temMaisAntigas = signal(false);
  carregandoAntigas = signal(false);
  carregando = signal(true);
  /** Chip "↓ Nova mensagem": aparece quando chega mensagem e o vendedor rolou para cima. */
  temNovaMensagem = signal(false);

  texto = signal('');
  enviando = signal(false);

  @ViewChild('thread') private threadEl?: ElementRef<HTMLDivElement>;
  @ViewChild('campo') private campoEl?: ElementRef<HTMLTextAreaElement>;

  private inscricoes: Subscription[] = [];

  constructor() {
    // O `untracked` é necessário: sem ele, os signals escritos dentro de `abrir` (mensagens,
    // carregando…) entrariam nas dependências do effect e ele reexecutaria em laço.
    effect(() => {
      const id = this.conversaId();
      untracked(() => this.abrir(id));
    });

    this.inscricoes.push(
      this.realtime.mensagemRecebida$.subscribe(m => {
        if (m.conversaId === this.conversaId()) this.recarregar('auto');
      }),
      // ACK só muda o tick: não pode mexer na posição de leitura.
      this.realtime.statusMensagem$.subscribe(() => this.recarregar('preservar'))
    );
  }

  ngOnDestroy() {
    this.inscricoes.forEach(s => s.unsubscribe());
    this.observador?.disconnect();
    // Blob que não é revogado vaza memória enquanto a aba viver — e uma thread com cem fotos
    // segura cem arquivos.
    for (const a of Object.values(this.anexos())) if (a.url) URL.revokeObjectURL(a.url);
    this.removerAnexo();
    // Sair da tela no meio de uma gravação não pode deixar o microfone aberto.
    clearInterval(this.cronometro);
    this.trilhas?.getTracks().forEach(t => t.stop());
    this.descartarAudio();
  }

  private abrir(conversaId: number) {
    this.texto.set('');
    this.temNovaMensagem.set(false);
    this.carregando.set(true);
    this.mensagens.set([]);

    this.servico.mensagens(conversaId).subscribe({
      next: p => {
        this.mensagens.set(p.itens);
        this.temMaisAntigas.set(p.temMais);
        this.carregando.set(false);
        this.aposRender(() => this.rolarParaFim(false));
      },
      error: () => this.carregando.set(false)
    });

    if (this.naoLidas() > 0) {
      this.servico.marcarLida(conversaId).subscribe(() => this.mudou.emit());
    }
  }

  /** 'fim' = rola para o fim (acabou de enviar) · 'auto' = rola só se já estava no fim ·
   *  'preservar' = não mexe na rolagem. */
  private recarregar(modo: 'fim' | 'auto' | 'preservar') {
    const id = this.conversaId();
    const jaNoFim = this.estaNoFim();
    // +1 para incluir a nova sem descartar o topo que o vendedor está lendo.
    const n = Math.max(30, this.mensagens().length + 1);

    this.servico.mensagens(id, undefined, n).subscribe(p => {
      this.mensagens.set(p.itens);
      this.temMaisAntigas.set(p.temMais);
      if (modo === 'fim') this.aposRender(() => this.rolarParaFim(true));
      else if (modo === 'auto') {
        if (jaNoFim) this.aposRender(() => this.rolarParaFim(true));
        else this.temNovaMensagem.set(true);
      }
    });
  }

  /** Prepende PRESERVANDO a posição de leitura: compensa o scrollTop pela altura inserida no
   *  topo. Sem essa compensação a thread pula e o vendedor perde o que estava lendo. */
  carregarAntigas() {
    const primeira = this.mensagens()[0];
    if (!primeira || this.carregandoAntigas()) return;

    this.carregandoAntigas.set(true);
    const el = this.threadEl?.nativeElement;
    const alturaAntes = el?.scrollHeight ?? 0;
    const topoAntes = el?.scrollTop ?? 0;

    this.servico.mensagens(this.conversaId(), primeira.id).subscribe({
      next: p => {
        this.mensagens.update(atual => [...p.itens, ...atual]);
        this.temMaisAntigas.set(p.temMais);
        this.carregandoAntigas.set(false);
        this.aposRender(() => {
          const e2 = this.threadEl?.nativeElement;
          if (e2) e2.scrollTop = topoAntes + (e2.scrollHeight - alturaAntes);
        });
      },
      error: () => this.carregandoAntigas.set(false)
    });
  }

  enviar() {
    if (this.enviando()) return;

    // COM ANEXO o caminho é outro: multipart, com progresso, e a legenda é o mesmo campo de
    // texto. Sem anexo, segue o envio de sempre.
    const f = this.anexo();
    if (f) { this.enviarAnexo(f); return; }

    const t = this.texto().trim();
    if (!t) return;

    this.enviando.set(true);
    this.servico.responder(this.conversaId(), t).subscribe({
      next: r => {
        this.texto.set('');
        this.enviando.set(false);
        // O campo esvaziou, mas a altura é estilo inline — sem isto ele fica alto e vazio.
        this.campoEl?.nativeElement.style.removeProperty('height');
        this.recarregar('fim');
        this.mudou.emit();
        if (!r.enviada) {
          // A mensagem EXISTE e aparece na thread marcada como "não chegou" — não é erro de
          // requisição, é falha de entrega. O toast diz isso sem bloquear.
          this.toast.erro(r.erro ?? 'A mensagem foi registrada mas não chegou ao WhatsApp.');
        }
      },
      error: e => {
        this.enviando.set(false);
        this.toast.erro(e.error?.erro ?? 'Não foi possível enviar.');
      }
    });
  }

  tick(m: MensagemDto) { return estadoDoAck(m.ack, m.enviadaEm, m.erro); }
  tickTitulo(m: MensagemDto) { return m.erro ?? rotuloAck(m.ack); }

  irParaFim() { this.rolarParaFim(true); }

  // ---------------------------------------------------------------- rolagem
  /** DEPOIS de cada render da lista: observa os balões com anexo que acabaram de existir.
   *
   *  Chamado de um lugar só — dentro do `aposRender` — porque a lista muda por três caminhos
   *  (abrir, recarregar, carregar anteriores) e ligar em cada um deixaria o quarto de fora.
   *  Foi o que aconteceu na primeira versão: `observarAnexos` existia e ninguém a chamava, e
   *  os anexos ficavam em "Carregando…" para sempre. */
  private aposRender(fn: () => void) {
    setTimeout(() => { fn(); this.observarAnexos(); }, 0);
  }

  private prefereReduzirMovimento(): boolean {
    return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
  }

  private rolarParaFim(suave: boolean) {
    const el = this.threadEl?.nativeElement;
    if (!el) return;
    this.temNovaMensagem.set(false);
    el.scrollTo({
      top: el.scrollHeight,
      behavior: suave && !this.prefereReduzirMovimento() ? 'smooth' : 'auto'
    });
  }

  /** O vendedor está no fim (ou a ~150px dele)? Sem container ainda = tratar como fim. */
  private estaNoFim(margem = 150): boolean {
    const el = this.threadEl?.nativeElement;
    if (!el) return true;
    return el.scrollHeight - el.scrollTop - el.clientHeight <= margem;
  }

  // ==================================================================== mídia (MID-1)
  /** Whitelist e teto do CLIENTE — conveniência, não segurança. Quem decide é o servidor, que
   *  olha os bytes; isto só evita subir 20 MB para receber "não pode" depois do upload. */
  private static readonly TIPOS = ['image/jpeg', 'image/png', 'image/webp', 'application/pdf'];
  private static readonly TETO = 16 * 1024 * 1024;

  anexo = signal<File | null>(null);
  previaUrl = signal<string | null>(null);
  erroAnexo = signal('');
  progresso = signal<number | null>(null);
  arrastando = signal(false);
  reenviando = signal<number | null>(null);

  /** Os blobs já baixados, por mensagem. `erro` distingue "ainda não busquei" de "não deu". */
  private anexos = signal<Record<number, { url?: string; erro?: boolean }>>({});
  anexoDe(id: number) { return this.anexos()[id]; }

  /** SOB DEMANDA: só quando o balão entra na tela.
   *
   *  Thread com cem fotos não pode baixar tudo — e a rota é autenticada, então cada uma é um
   *  GET com Bearer que vira `blob:` local. Um `<img src>` apontando para `/api/midia` não
   *  carregaria cabeçalho nenhum, e a alternativa seria abrir a rota, que não se faz. */
  carregarMidia(id: number, forcar = false) {
    const atual = this.anexos()[id];
    if (atual && !forcar && !atual.erro) return;
    if (atual?.url) URL.revokeObjectURL(atual.url);

    this.anexos.update(m => ({ ...m, [id]: {} }));

    this.servico.midia(id).subscribe({
      next: b => this.anexos.update(m => ({ ...m, [id]: { url: URL.createObjectURL(b) } })),
      // Falha NÃO some: vira marcador com "tentar de novo". Silêncio faria a mensagem parecer
      // que nunca existiu.
      error: () => this.anexos.update(m => ({ ...m, [id]: { erro: true } }))
    });
  }

  /** Observa os balões com anexo e busca o blob quando entram na viewport. */
  private observador?: IntersectionObserver;

  private ligarObservador() {
    if (this.observador || typeof IntersectionObserver === 'undefined') return;

    this.observador = new IntersectionObserver(entradas => {
      for (const e of entradas) {
        if (!e.isIntersecting) continue;
        const id = Number((e.target as HTMLElement).dataset['msg'] ?? 0);
        if (id) this.carregarMidia(id);
        this.observador!.unobserve(e.target);
      }
    }, { rootMargin: '200px' });   // começa um pouco antes de aparecer, para não piscar
  }

  private observarAnexos() {
    this.ligarObservador();
    if (!this.observador) {
      // Sem IntersectionObserver (jsdom, navegador antigo): carrega tudo. Pior em desempenho,
      // melhor que anexo que nunca aparece.
      for (const m of this.mensagens()) if (m.tipoMidia !== 'nenhum') this.carregarMidia(m.id);
      return;
    }
    for (const el of this.threadEl?.nativeElement.querySelectorAll('.midia[data-msg]') ?? []) {
      this.observador.observe(el);
    }
  }

  tamanho(m: MensagemDto): string {
    return m.midiaBytes ? this.emKb(m.midiaBytes) : '';
  }

  emKb(bytes: number): string {
    return bytes >= 1024 * 1024
      ? `${(bytes / (1024 * 1024)).toFixed(1)} MB`
      : `${Math.max(1, Math.round(bytes / 1024))} KB`;
  }

  // ---------------------------------------------------------------- escolher
  aoEscolher(ev: Event) {
    const f = (ev.target as HTMLInputElement).files?.[0];
    if (f) this.aceitar(f);
    (ev.target as HTMLInputElement).value = '';   // permite reescolher o mesmo arquivo
  }

  aoArrastar(ev: DragEvent) {
    ev.preventDefault();
    this.arrastando.set(true);
  }

  aoSoltar(ev: DragEvent) {
    ev.preventDefault();
    this.arrastando.set(false);
    const f = ev.dataTransfer?.files?.[0];
    if (f) this.aceitar(f);
  }

  private aceitar(f: File) {
    this.erroAnexo.set('');

    if (f.size > Thread.TETO) {
      this.erroAnexo.set('O arquivo passa de 16 MB, que é o limite do WhatsApp.');
      return;
    }
    if (!Thread.TIPOS.includes(f.type)) {
      this.erroAnexo.set('Dá para enviar imagem (JPG, PNG ou WEBP) e PDF.');
      return;
    }

    this.removerAnexo();
    this.anexo.set(f);
    if (f.type.startsWith('image/')) this.previaUrl.set(URL.createObjectURL(f));
  }

  removerAnexo() {
    const u = this.previaUrl();
    if (u) URL.revokeObjectURL(u);
    this.previaUrl.set(null);
    this.anexo.set(null);
    this.progresso.set(null);
  }

  // ---------------------------------------------------------------- enviar
  private enviarAnexo(f: File) {
    this.enviando.set(true);
    this.progresso.set(0);
    this.erroAnexo.set('');

    this.servico.enviarMidia(this.conversaId(), f, this.texto()).subscribe({
      next: ev => {
        if (ev.type === HttpEventType.UploadProgress && ev.total) {
          this.progresso.set(Math.round((100 * ev.loaded) / ev.total));
        } else if (ev.type === HttpEventType.Response) {
          this.enviando.set(false);
          this.removerAnexo();
          this.texto.set('');
          // A linha EXISTE mesmo se a entrega falhou — a thread a mostra com "tentar de novo".
          if (ev.body && !ev.body.enviada) this.toast.erro(ev.body.erro ?? 'Não foi possível enviar.');
          this.recarregar('preservar');
        }
      },
      error: e => {
        this.enviando.set(false);
        this.progresso.set(null);
        this.erroAnexo.set(e.error?.erro ?? 'Não foi possível enviar o arquivo.');
      }
    });
  }

  tentarDeNovo(m: MensagemDto) {
    this.reenviando.set(m.id);
    this.servico.reenviar(m.id).subscribe({
      next: r => {
        this.reenviando.set(null);
        if (!r.enviada) this.toast.erro(r.erro ?? 'Ainda não foi.');
        this.recarregar('preservar');
      },
      error: e => {
        this.reenviando.set(null);
        this.toast.erro(e.error?.erro ?? 'Não foi possível reenviar.');
      }
    });
  }


  // ==================================================================== gravação (bloco 13)
  /** ===================== O FORMATO, DECIDIDO NO CLIENTE =====================
   *  O WhatsApp só trata como nota de voz o OGG/Opus. O `MediaRecorder` entrega o que o
   *  navegador tem:
   *
   *    Firefox   audio/ogg;codecs=opus    -> já é o certo
   *    Chrome    audio/webm;codecs=opus   -> Opus em outro contêiner; o servidor reempacota
   *    Safari    audio/mp4 (AAC)          -> outro CODEC, e aí não dá
   *
   *  A ordem abaixo pede o melhor primeiro. Nenhum suportado = o botão avisa e não grava, em
   *  vez de mandar um arquivo que o cliente recebe como anexo e não ouve. */
  private static readonly FORMATOS = [
    'audio/ogg;codecs=opus',
    'audio/webm;codecs=opus',
    'audio/webm'
  ];

  gravando = signal(false);
  segundosGravados = signal(0);
  audioGravado = signal<{ blob: Blob; url: string; segundos: number } | null>(null);

  private gravador?: MediaRecorder;
  private pedacos: Blob[] = [];
  private cronometro?: ReturnType<typeof setInterval>;
  private trilhas?: MediaStream;

  /** O teto de 5 minutos, também no cliente: parar sozinho é melhor que subir 5 MB para o
   *  servidor recusar. A regra que VALE continua sendo a de lá. */
  private static readonly LIMITE_S = 300;

  private formatoSuportado(): string | null {
    if (typeof MediaRecorder === 'undefined') return null;
    return Thread.FORMATOS.find(f => MediaRecorder.isTypeSupported(f)) ?? null;
  }

  async iniciarGravacao() {
    this.erroAnexo.set('');

    const formato = this.formatoSuportado();
    if (!formato) {
      // Safari/iOS cai aqui. Dizer O QUE FAZER, não só "não suportado".
      this.erroAnexo.set(
        'Este navegador não grava áudio no formato do WhatsApp. Use o Chrome, ou grave pelo celular.');
      return;
    }

    let trilhas: MediaStream;
    try {
      trilhas = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch {
      // ===== PERMISSÃO NEGADA NÃO PODE FALHAR EM SILÊNCIO =====
      // Sem esta mensagem, clicar no microfone não faz nada e o vendedor conclui que o botão
      // está quebrado. Cobre também "sem microfone" e "dispositivo em uso".
      this.erroAnexo.set(
        'Não foi possível acessar o microfone. Autorize o uso no navegador e tente de novo.');
      return;
    }

    this.trilhas = trilhas;
    this.pedacos = [];
    this.segundosGravados.set(0);
    this.gravando.set(true);

    this.gravador = new MediaRecorder(trilhas, { mimeType: formato });
    this.gravador.ondataavailable = e => { if (e.data.size) this.pedacos.push(e.data); };
    this.gravador.onstop = () => this.fecharGravacao(formato);
    this.gravador.start();

    this.cronometro = setInterval(() => {
      const s = this.segundosGravados() + 1;
      this.segundosGravados.set(s);
      if (s >= Thread.LIMITE_S) this.pararGravacao();
    }, 1000);
  }

  pararGravacao() {
    if (!this.gravando()) return;
    this.gravador?.stop();          // dispara `onstop`, que monta a prévia
  }

  /** Descartar DURANTE a gravação: para, solta o microfone e não deixa prévia. */
  cancelarGravacao() {
    this.pedacos = [];
    this.pararGravacao();
    this.audioGravado.set(null);
  }

  private fecharGravacao(formato: string) {
    clearInterval(this.cronometro);
    this.gravando.set(false);
    // SOLTAR O MICROFONE: sem isto o indicador de gravação fica aceso no navegador depois de
    // parar, e o vendedor pensa que ainda está sendo ouvido.
    this.trilhas?.getTracks().forEach(t => t.stop());
    this.trilhas = undefined;

    const segundos = this.segundosGravados();
    if (this.pedacos.length === 0 || segundos < 1) { this.pedacos = []; return; }

    const blob = new Blob(this.pedacos, { type: formato });
    this.pedacos = [];
    this.audioGravado.set({ blob, url: URL.createObjectURL(blob), segundos });
  }

  descartarAudio() {
    const a = this.audioGravado();
    if (a) URL.revokeObjectURL(a.url);
    this.audioGravado.set(null);
  }

  enviarAudio() {
    const a = this.audioGravado();
    if (!a || this.enviando()) return;

    this.enviando.set(true);
    this.erroAnexo.set('');

    this.servico.enviarAudio(this.conversaId(), a.blob, `audio-${Date.now()}.ogg`).subscribe({
      next: r => {
        this.enviando.set(false);
        this.descartarAudio();
        // A linha EXISTE mesmo se a entrega falhou — a thread a mostra com "tentar de novo".
        if (!r.enviada) this.toast.erro(r.erro ?? 'O áudio foi registrado mas não chegou.');
        this.recarregar('fim');
        this.mudou.emit();
      },
      error: e => {
        this.enviando.set(false);
        this.erroAnexo.set(e.error?.erro ?? 'Não foi possível enviar o áudio.');
      }
    });
  }

  /** O campo cresce com o texto, até o teto do CSS.
   *
   *  `height = 'auto'` ANTES de ler `scrollHeight` não é redundante: sem isso o valor lido é o
   *  da altura atual, e o campo só sabe crescer — apagar três linhas o deixaria alto para
   *  sempre.
   *
   *  O teto vem do `max-height` do CSS, não de um número aqui: dois lugares com a mesma medida
   *  divergem, e a versão do CSS é a que o `overflow-y` respeita. */
  ajustarAltura(campo: HTMLTextAreaElement) {
    campo.style.height = 'auto';
    campo.style.height = `${campo.scrollHeight}px`;
  }

  /** "1:07". `Math.floor` e não `round` nos segundos: mostrar 1:60 seria absurdo. */
  minutos(segundos: number): string {
    const m = Math.floor(segundos / 60);
    const s = Math.floor(segundos % 60);
    return `${m}:${String(s).padStart(2, '0')}`;
  }

}
