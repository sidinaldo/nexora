import {
  Component, ElementRef, OnDestroy, OnInit, ViewChild, computed, inject, output, signal
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  Paginacao, alturaMinimaDaTabela, fatiar, rolarParaTopoDaTabela, totalDePaginas
} from '../../nucleo/paginacao/paginacao';
import { CanaisServico } from '../../nucleo/servicos/canais.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { baixarBlob } from '../../nucleo/download';
import {
  Canais as CanaisDto, CanalDto, LIMITE_MENSAGEM_CANAL, OrigemLead
} from '../../nucleo/modelos';

/** CANAIS DE CAPTAÇÃO — o painel da aba "QR Code e links" em `/captacao`.
 *
 *  ===================== É PAINEL, NÃO PÁGINA (NAV-1) =====================
 *  Nasceu com rota própria (`/canais`) e cabeçalho de página. Virou uma aba de Captação e perdeu
 *  `.pagina`, `<h1>` e o subtítulo — quem desenha o cabeçalho agora é o container. Continua
 *  buscando a própria lista e funcionando sozinho; `mudou` avisa o container para recalcular o
 *  resumo depois de qualquer escrita.
 *  ========================================================================
 *
 *  ===================== O QUE ESTA TELA PRECISA DEIXAR CLARO =====================
 *  O rastreio é FRÁGIL de propósito, e a tela não pode esconder isso. O canal gera um link
 *  `wa.me` com um código curto no texto pré-preenchido; quem escaneia pode apagar o texto antes
 *  de mandar, e vai acontecer. Quando acontece, o lead entra como `whatsapp` e ninguém fica
 *  sabendo de onde veio — que é melhor que atribuir ao canal errado.
 *
 *  Por isso o contador é apresentado como PISO, não como total, e o texto do link fica visível:
 *  quem cria o canal precisa ver a frase que o cliente dele vai mandar, porque é ela que decide
 *  se o código sobrevive ao envio.
 *  ================================================================================ */
@Component({
  selector: 'app-canais',
  imports: [FormsModule, Paginacao],
  templateUrl: './canais.html',
  styleUrl: './canais.css'
})
export class Canais implements OnInit, OnDestroy {
  private servico = inject(CanaisServico);
  private toast = inject(ToastServico);

  /** Alguma escrita aconteceu. O container de Captação usa isto para recalcular o resumo — que
   *  soma os dois canais e ficaria velho sem o aviso. */
  mudou = output<void>();

  readonly origens: { valor: OrigemLead; rotulo: string }[] = [
    { valor: 'qrcode', rotulo: 'QR Code (balcão, panfleto, vitrine)' },
    { valor: 'instagram', rotulo: 'Instagram (link na bio, story)' },
    { valor: 'facebook', rotulo: 'Facebook' },
    { valor: 'google', rotulo: 'Google (anúncio, perfil da empresa)' },
    { valor: 'site', rotulo: 'Site' },
    { valor: 'indicacao', rotulo: 'Indicação (parceiro)' },
    { valor: 'outro', rotulo: 'Outro' }
  ];

  lista = signal<CanalDto[]>([]);
  conexoes = signal<CanaisDto['conexoes']>([]);
  podeCriar = signal(false);
  leadsAtribuidos = signal(0);

  carregando = signal(true);
  erro = signal('');

  // ---- criação
  fNome = signal('');
  fConexaoId = signal<number | null>(null);
  fOrigem = signal<OrigemLead>('qrcode');
  /** A frase do link. Vazia = a Nexora usa a padrao ("Olá! Tenho interesse."). O codigo entra
   *  sozinho no fim, sempre — ver `CodigoCanal.TextoDoLink`. */
  fMensagem = signal('');

  /** O teto, para o `maxlength` e o contador. Vem do modelo compartilhado — o servidor tem o
   *  mesmo numero e e ele quem recusa. */
  readonly limite = LIMITE_MENSAGEM_CANAL;
  criando = signal(false);
  erroNovo = signal('');

  // ---- edição em linha
  editandoId = signal<number | null>(null);
  eNome = signal('');
  eConexaoId = signal<number | null>(null);
  eOrigem = signal<OrigemLead>('qrcode');
  eMensagem = signal('');

  /** Qual canal está com o QR aberto. Um por vez: dois QR na tela ao mesmo tempo é convite para
   *  imprimir o errado. */
  abertoId = signal<number | null>(null);
  aberto = computed<CanalDto | null>(
    () => this.lista().find(c => c.id === this.abertoId()) ?? null);

  /** O `blob:` do SVG que está sendo exibido. Object URL e não `<img src="/api/...">` porque a
   *  rota exige `Authorization: Bearer`, e `<img>` navega sem cabeçalho. */
  qrUrl = signal<string | null>(null);
  carregandoQr = signal(false);

  removendo = signal<CanalDto | null>(null);

  /** O canal cujo número deixou de estar pareado: o link dele está quebrado AGORA, e o material
   *  já impresso aponta para um número que não atende. */
  semNumero = computed(() => this.lista().filter(c => c.numero === null));

  /** ===================== PAGINAÇÃO NO CLIENTE =====================
   *  `GET /api/canais` devolve a lista inteira, e o serviço limita a 30 por empresa. O recorte
   *  existe pelo mesmo motivo das outras tabelas: o comportamento é o mesmo em toda tela do
   *  painel, e ninguém precisa aprender de novo.
   *  ================================================================ */
  pagina = signal(1);

  @ViewChild('tabelaTopo') private tabelaTopo?: ElementRef<HTMLElement>;

  totalPaginas = computed(() => totalDePaginas(this.lista().length));
  visiveis = computed(() => fatiar(this.lista(), this.pagina()));
  alturaMinima = computed(() => this.totalPaginas() > 1 ? alturaMinimaDaTabela() : 0);

  irPara(p: number) {
    this.pagina.set(p);
    rolarParaTopoDaTabela(this.tabelaTopo?.nativeElement);
  }

  ngOnInit() { this.carregar(); }

  ngOnDestroy() { this.soltarQr(); }

  // ---------------------------------------------------------------- lista
  carregar() {
    this.servico.listar().subscribe({
      next: r => {
        this.lista.set(r.itens);
        this.conexoes.set(r.conexoes);
        this.podeCriar.set(r.podeCriar);
        this.leadsAtribuidos.set(r.leadsAtribuidos);
        this.carregando.set(false);
        this.erro.set('');

        if (this.fConexaoId() === null && r.conexoes.length > 0) {
          this.fConexaoId.set(r.conexoes[0].id);
        }

        // QR aberto sobre um canal que sumiu (apagado em outra aba): fecha em vez de mostrar
        // imagem velha.
        if (this.abertoId() !== null && !r.itens.some(c => c.id === this.abertoId())) {
          this.fechar();
        }

        // A lista encolheu e a pessoa estava na última página: sem isto ela fica olhando para
        // uma tabela vazia com o controle dizendo "página 2 de 1".
        if (this.pagina() > this.totalPaginas()) this.pagina.set(this.totalPaginas());
      },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Não foi possível carregar os canais.');
        this.carregando.set(false);
      }
    });
  }

  /** Recarrega E avisa o container. Um método só para as escritas: se cada uma chamasse
   *  `carregar()` na mão, bastaria uma esquecer o `mudou` para o resumo de Captação ficar velho
   *  — e resumo velho não parece defeito, parece número. */
  private aposEscrita() {
    this.carregar();
    this.mudou.emit();
  }

  // ---------------------------------------------------------------- QR
  abrir(c: CanalDto) {
    if (this.abertoId() === c.id) { this.fechar(); return; }

    this.soltarQr();
    this.abertoId.set(c.id);

    if (c.link === null) return;   // sem número pareado não há QR; a tela explica

    this.carregandoQr.set(true);
    this.servico.svg(c.id).subscribe({
      next: b => {
        this.qrUrl.set(URL.createObjectURL(b));
        this.carregandoQr.set(false);
      },
      error: e => {
        this.carregandoQr.set(false);
        this.erro.set(e.error?.erro ?? 'Não foi possível gerar o QR Code.');
      }
    });
  }

  fechar() {
    this.soltarQr();
    this.abertoId.set(null);
  }

  /** Devolve o blob ao navegador. Sem isto, cada abertura deixa uma imagem presa na memória da
   *  aba até o recarregamento. */
  private soltarQr() {
    const url = this.qrUrl();
    if (url) URL.revokeObjectURL(url);
    this.qrUrl.set(null);
  }

  baixarSvg(c: CanalDto) {
    this.servico.svg(c.id).subscribe({
      next: b => baixarBlob(`${c.nomeArquivo}.svg`, b),
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível baixar o SVG.')
    });
  }

  baixarPng(c: CanalDto) {
    this.servico.png(c.id).subscribe({
      next: b => baixarBlob(`${c.nomeArquivo}.png`, b),
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível baixar o PNG.')
    });
  }

  // ---------------------------------------------------------------- criar
  criar() {
    const nome = this.fNome().trim();
    const conexaoId = this.fConexaoId();

    if (nome.length < 2) { this.erroNovo.set('Dê um nome ao canal.'); return; }
    if (conexaoId === null) { this.erroNovo.set('Escolha o número que vai atender.'); return; }

    this.criando.set(true);
    this.erroNovo.set('');
    this.servico.criar(nome, conexaoId, this.fOrigem(), this.fMensagem().trim() || null).subscribe({
      next: r => {
        this.criando.set(false);
        this.fNome.set('');
        this.toast.sucesso(`"${nome}" criado. Baixe o QR Code e o link.`);
        this.mudou.emit();

        // Lista recarregada aqui em vez de `aposEscrita()` porque é preciso ACHAR o recém-criado
        // na resposta para abrir o QR dele — quem acabou de criar veio buscar a imagem.
        this.servico.listar().subscribe(l => {
          this.lista.set(l.itens);
          this.podeCriar.set(l.podeCriar);
          this.leadsAtribuidos.set(l.leadsAtribuidos);
          const novo = l.itens.find(c => c.id === r.id);
          if (novo) this.abrir(novo);
        });
      },
      error: e => {
        this.criando.set(false);
        this.erroNovo.set(e.error?.erro ?? 'Não foi possível criar.');
      }
    });
  }

  // ---------------------------------------------------------------- editar
  editar(c: CanalDto) {
    this.editandoId.set(c.id);
    this.eNome.set(c.nome);
    this.eConexaoId.set(c.conexaoId);
    this.eOrigem.set(c.origem);
    // A FRASE, sem o codigo — `c.texto` traz o resultado final e recolocá-lo aqui duplicaria o
    // codigo a cada edicao.
    this.eMensagem.set(c.mensagem ?? '');
  }

  cancelarEdicao() { this.editandoId.set(null); }

  salvarEdicao(c: CanalDto) {
    const conexaoId = this.eConexaoId();
    if (conexaoId === null) { this.toast.erro('Escolha o número que vai atender.'); return; }

    // Trocar o número TROCA O LINK, e o QR já impresso continua apontando para o antigo. Não é
    // proibido — pode ser exatamente o que a empresa quer, se aposentou o número velho —, mas
    // quem faz precisa saber o preço antes, não depois.
    if (conexaoId !== c.conexaoId && !confirm(
      `Mudar "${c.nome}" para outro número?\n\n` +
      `O link muda AGORA. Todo QR Code já impresso continua apontando para o número antigo, e ` +
      `não há como corrigir o que já foi distribuído.`)) return;

    this.servico.atualizar(c.id, this.eNome().trim(), conexaoId, this.eOrigem(),
                          this.eMensagem().trim() || null).subscribe({
      next: () => {
        this.editandoId.set(null);
        this.toast.sucesso('Canal atualizado.');
        this.fechar();
        this.aposEscrita();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível salvar.')
    });
  }

  alternarAtivo(c: CanalDto) {
    if (c.ativo && !confirm(
      `Desativar "${c.nome}"?\n\n` +
      `O link e o QR continuam funcionando — quem escanear ainda cai na sua conversa. O que para ` +
      `é a ATRIBUIÇÃO: os leads passam a entrar como "whatsapp", sem dizer que vieram daqui.`)) return;

    this.servico.alternarAtivo(c.id, !c.ativo).subscribe({
      next: () => {
        this.toast.sucesso(c.ativo
          ? `"${c.nome}" desativado. Os leads continuam entrando, sem atribuição.`
          : `"${c.nome}" ativado.`);
        this.aposEscrita();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível alterar.')
    });
  }

  // ---------------------------------------------------------------- remover
  pedirRemocao(c: CanalDto) { this.removendo.set(c); }
  cancelarRemocao() { this.removendo.set(null); }

  confirmarRemocao() {
    const alvo = this.removendo();
    if (alvo === null) return;

    this.servico.remover(alvo.id).subscribe({
      next: () => {
        this.removendo.set(null);
        if (this.abertoId() === alvo.id) this.fechar();
        this.toast.sucesso(`"${alvo.nome}" apagado.`);
        this.aposEscrita();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível apagar.')
    });
  }

  // ---------------------------------------------------------------- apoio
  copiar(texto: string | null, oque: string) {
    if (texto === null) return;
    navigator.clipboard.writeText(texto).then(
      () => this.toast.sucesso(`${oque} copiado.`),
      () => this.toast.erro('Não foi possível copiar. Selecione e copie à mão.')
    );
  }

  rotuloOrigem(origem: OrigemLead): string {
    return this.origens.find(o => o.valor === origem)?.rotulo ?? origem;
  }
}
